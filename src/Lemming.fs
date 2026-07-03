module FsLemming.Lemming

open FsLemming.Types

/// Each lemming is its own actor with fully private state. The outside world
/// can only: tell it the current terrain (and get back its intended move), or
/// change its skill. Nothing else can read or write `State`.
type private State =
    { Id: int
      X: int
      Y: int
      Dir: int // facing: -1 left, +1 right
      Skill: Skill
      Counter: int // skill work left (e.g. bricks remaining for a Builder)
      FallDist: int // pixels fallen so far (for splat / floater)
      Fuse: int // bomb fuse ticks remaining (0 = unarmed); independent of Skill
      Submerged: int // ticks spent sinking in water (drowns past the threshold)
      Alive: bool }

/// A lemming floats/sinks in water this many ticks, then drowns.
let private drownAfter = 14

/// A fall longer than this many pixels is fatal on landing (unless Floater).
let private safeFall = 28

/// A walker auto-climbs a rise this many pixels tall (gentle bumps/slopes);
/// anything taller is a wall — turn around (or, for a Climber, scale it).
let private maxStepUp = 3

/// True the tick a Bomber's fuse reaches zero (and it's a live, on-map lemming).
/// Shared by `step` (to detonate) and the agent (to report it for particles), so
/// the two can't disagree.
let private fuseExpired (terrain: TerrainSnapshot) (s: State) =
    s.Alive && s.Y < terrain.Height && s.Fuse = 1

/// Starting counter value for a freshly-assigned skill (0 for skills that don't
/// use one). Builders get a fixed number of bricks.
let private initialCounter =
    function
    | Builder -> 12 // bricks
    | _ -> 0

type private Msg =
    | Tick of TerrainSnapshot * AsyncReplyChannel<TickResult>
    | SetSkill of Skill
    | Detonate of fuse: int // become a Bomber with a specific fuse (for Nuke)
    | QuerySkill of AsyncReplyChannel<Skill * int> // current (skill, fuse), read-only
    | Stop // graceful shutdown: end the loop so the agent terminates

/// Pure decision function: given the terrain and current state, what does this
/// lemming do this tick? Returns the next state and any terrain edits it wants.
/// Being pure makes it trivial to unit-test the behaviour without any actors.
let private step (terrain: TerrainSnapshot) (s: State) : State * Mutation list =
    if not s.Alive then
        s, []
    // Fell off the bottom of the map -> gone.
    elif s.Y >= terrain.Height then
        { s with Alive = false }, []
    // An armed fuse burns every tick, wherever the lemming is; at zero it explodes
    // immediately — even mid-fall. Handled before physics so a falling/blocking
    // lemming still detonates in place (and a Nuke reliably gets everyone).
    elif fuseExpired terrain s then
        { s with Alive = false }, [ RemoveCircle(s.X, s.Y, 8) ]
    // Hazards override behaviour: lava kills at once; water lets it sink, then drown.
    elif terrain.HazardAt(s.X, s.Y) = Some Lava then
        { s with Alive = false }, []
    elif terrain.HazardAt(s.X, s.Y) = Some Water then
        // The fuse keeps burning underwater too — see the comment above.
        let s = if s.Fuse > 1 then { s with Fuse = s.Fuse - 1 } else s

        if s.Submerged >= drownAfter then
            { s with Alive = false }, []
        else
            { s with Submerged = s.Submerged + 1; Y = s.Y + (s.Submerged % 2) }, [] // sink slowly
    else
        // Burn one fuse tick if armed; the lemming keeps doing whatever its skill
        // is (a bombed Blocker keeps blocking and stays put, etc.).
        let s = if s.Fuse > 1 then { s with Fuse = s.Fuse - 1 } else s
        let s = if s.Submerged > 0 then { s with Submerged = 0 } else s // climbed out of water

        // Climbers cling to a wall ahead and scale it, ignoring gravity.
        if s.Skill = Climber && terrain.IsSolid(s.X + s.Dir, s.Y) then
            if terrain.IsSolid(s.X + s.Dir, s.Y - 1) then
                { s with Y = s.Y - 1; FallDist = 0 }, [] // keep climbing
            else
                { s with X = s.X + s.Dir; Y = s.Y - 1; FallDist = 0 }, [] // top out onto the ledge
        // Diggers carve straight down a slice at a time while there's ground below,
        // staying in the shaft; they stop (revert to Walker) the moment they break
        // through into open space — so they dig through one floor, not to the void.
        elif s.Skill = Digger then
            // Dig down while there's solid within 3px below; a 3px+ gap means we've
            // broken through into open space -> revert to Walker (then fall). Hitting
            // indestructible steel also stops the dig.
            if terrain.IsSteel(s.X, s.Y + 1) then
                { s with Skill = Walker }, []
            elif [ 1..3 ] |> List.exists (fun k -> terrain.IsSolid(s.X, s.Y + k)) then
                { s with Y = s.Y + 1; FallDist = 0 }, [ RemoveRect(s.X - 3, s.Y + 1, 7, 1) ]
            else
                { s with Skill = Walker }, []
        elif s.Skill = Miner then
            // Dig diagonally down-forward while there's solid within 3px down-ahead;
            // a 3px+ gap there means we've broken through -> revert to Walker. Steel
            // ahead stops the dig.
            let d = s.Dir

            // Steel anywhere in the column ahead (the full carved height) is
            // impassable — the removal would skip it, so stop rather than walk
            // through an uncarved cell.
            if [ -10 .. 1 ] |> List.exists (fun k -> terrain.IsSteel(s.X + d, s.Y + k)) then
                { s with Skill = Walker }, []
            elif [ 1..3 ] |> List.exists (fun k -> terrain.IsSolid(s.X + d * k, s.Y + k)) then
                // Carve the head/body column ahead AND one row below the feet
                // (Y-10..Y+1), so mining straight through a thin floor leaves no
                // 1px shelf behind. The next tick's probe looks at Y+2..Y+4 (from
                // the new, lower position), which stays outside this cleared band —
                // so continuation still works and the miner doesn't quit early.
                let ax = if d > 0 then s.X else s.X - 7
                { s with X = s.X + d; Y = s.Y + 1; FallDist = 0 }, [ RemoveRect(ax, s.Y - 10, 8, 12) ]
            else
                { s with Skill = Walker }, []
        // Nothing solid directly below -> fall.
        elif not (terrain.IsSolid(s.X, s.Y + 1)) then
            let nf = s.FallDist + 1
            // The parachute only deploys past a small step (a 1-3px drop falls normally).
            if s.Skill = Floater && s.FallDist > 3 && s.FallDist % 2 = 1 then
                { s with FallDist = nf }, [] // floater drifts down at half speed
            else
                { s with Y = s.Y + 1; FallDist = nf }, []
        // Landed after too long a fall (and not a Floater) -> splat.
        elif s.FallDist > safeFall && s.Skill <> Floater then
            { s with Alive = false }, []
        else
            // On solid ground; reset the fall counter, then act on the skill.
            let s = { s with FallDist = 0 }

            match s.Skill with
            | Blocker ->
                // Stand firm. (Its position is reported to the World each tick so
                // other walkers see this cell as impassable.)
                s, []
            | Digger
            | Miner -> s, [] // handled before gravity (dig down / diagonal)
            | Builder ->
                if s.Counter > 0 then
                    // Lay one brick just ahead at foot level, then step up onto it.
                    let bx = if s.Dir > 0 then s.X + 1 else s.X - 4
                    let brick = AddTerrain(bx, s.Y, 4, 1)
                    { s with X = s.X + s.Dir; Y = s.Y - 1; Counter = s.Counter - 1 }, [ brick ]
                else
                    { s with Skill = Walker }, [] // out of bricks -> walk on
            | Basher ->
                // Tunnel horizontally. We keep bashing as long as there's terrain
                // within a few px ahead — so you can assign it a little BEFORE the
                // wall and it walks up and digs in — clearing body height but leaving
                // the floor (row Y+1). It reverts to Walker once it breaks out into a
                // clear gap, so it digs through one wall, not every wall after it.
                let d = s.Dir

                let wallWithinReach =
                    [ 1..20 ]
                    |> List.exists (fun k -> terrain.IsSolid(s.X + d * k, s.Y) || terrain.IsSolid(s.X + d * k, s.Y - 5))

                // Steel anywhere in the column ahead (the full carved height) is
                // impassable — the removal would skip it, so stop bashing rather
                // than walk through an uncarved cell.
                let steelAhead =
                    [ -10 .. 0 ] |> List.exists (fun k -> terrain.IsSteel(s.X + d, s.Y + k))

                if steelAhead || not wallWithinReach then
                    { s with Skill = Walker }, [] // hit steel, or broke out into open space
                else
                    let ax = if d > 0 then s.X + 1 else s.X - 7
                    { s with X = s.X + d }, [ RemoveRect(ax, s.Y - 10, 8, 11) ]
            // Walkers — plus on-the-ground Climbers, Floaters and counting-down
            // Bombers — all just walk. (Climbing/drift/fuse are handled above.)
            | Walker
            | Climber
            | Floater
            | Bomber ->
                let nx = s.X + s.Dir

                if nx < 0 || nx >= terrain.Width || terrain.IsBlocked(nx, s.Y) then
                    { s with Dir = -s.Dir }, [] // map edge or a blocking lemming
                elif not (terrain.IsSolid(nx, s.Y)) then
                    { s with X = nx }, [] // clear ahead — a small drop is handled by gravity
                else
                    // A rise ahead: auto-step up if it's no taller than maxStepUp.
                    let rec rise u =
                        if u > maxStepUp then None
                        elif not (terrain.IsSolid(nx, s.Y - u)) then Some u
                        else rise (u + 1)

                    match rise 1 with
                    | Some u -> { s with X = nx; Y = s.Y - u }, [] // step up the bump
                    | None -> { s with Dir = -s.Dir }, [] // too tall — turn (Climbers scale it, handled above)

/// The public, immutable snapshot of a lemming (for rendering / coordination).
let private toView (s: State) : LemmingView =
    { Id = s.Id
      X = s.X
      Y = s.Y
      Dir = s.Dir
      Skill = s.Skill
      FallDist = s.FallDist
      Fuse = s.Fuse
      Alive = s.Alive }

type Lemming(id: int, x: int, y: int) =

    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop (s: State) =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | Stop -> return () // end the loop -> the agent terminates
                    | _ ->
                        // Compute the next state inside try/with so a thrown message
                        // can never kill the agent (which would hang the tick awaiting
                        // its reply). On failure we log loudly, answer safely, carry
                        // on. The recursion is OUTSIDE the try, so handlers don't pile up.
                        let next =
                            try
                                match msg with
                                | SetSkill Bomber -> { s with Fuse = 40 } // light the fuse, keep current skill
                                | SetSkill skill -> { s with Skill = skill; Counter = initialCounter skill }
                                | Detonate fuse -> { s with Fuse = fuse }
                                | QuerySkill reply ->
                                    reply.Reply(s.Skill, s.Fuse) // read-only; state unchanged
                                    s
                                | Tick(terrain, reply) ->
                                    let s', mutations = step terrain s

                                    // Classify a death this tick, for the right particle effect.
                                    let effect =
                                        if fuseExpired terrain s then
                                            Some Explode
                                        elif s.Alive && not s'.Alive then
                                            match terrain.HazardAt(s.X, s.Y) with
                                            | Some Lava -> Some Burn
                                            | Some Water -> Some Drown
                                            | None -> if s'.Y < terrain.Height then Some Splat else None
                                        else
                                            None

                                    reply.Reply { View = toView s'; Mutations = mutations; Effect = effect }
                                    s'
                                | Stop -> s // unreachable (handled above)
                            with ex ->
                                eprintfn "Lemming %d failed on a message: %O" s.Id ex
                                // Still answer a pending Tick so the coordinator's
                                // Async.Parallel doesn't wait forever.
                                match msg with
                                | Tick(_, reply) -> reply.Reply { View = toView s; Mutations = []; Effect = None }
                                | QuerySkill reply -> reply.Reply(s.Skill, s.Fuse)
                                | _ -> ()

                                s

                        return! loop next
                }

            loop
                { Id = id
                  X = x
                  Y = y
                  Dir = 1
                  Skill = Walker
                  Counter = 0
                  FallDist = 0
                  Fuse = 0
                  Submerged = 0
                  Alive = true })

    member _.Id = id
    member _.Tick(terrain) = agent.PostAndAsyncReply(fun rc -> Tick(terrain, rc))
    member _.SetSkill(skill) = agent.Post(SetSkill skill)
    /// Current (skill, fuse) — used to avoid spending a unit on a redundant assign.
    member _.Current = agent.PostAndAsyncReply QuerySkill
    /// Arm this lemming as a Bomber with a given fuse — used by the Nuke.
    member _.Detonate(fuse) = agent.Post(Detonate fuse)
    /// Gracefully shut this lemming's agent down (called when it's retired).
    member _.Stop() = agent.Post Stop
