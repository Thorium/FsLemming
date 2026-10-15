module FsLemming.LevelCheck

// The level check behind the editor's Check button and tools/levels.fsx: is a
// level fair, and can it be solved? It runs ONE lemming with the game's own
// rules — Lemming.step and World.applyMutation, called directly instead of
// through the actors — and searches for a plan of skill assignments that gets
// it to the exit. Not part of the game itself: only the editor and the tools
// load this file.

open System.Collections.Generic
open FsLemming.Types

/// How serious a finding is.
[<Struct>]
type Severity =
    /// the level can't be played as meant
    | Problem
    /// it plays, but probably not as meant
    | Warning
    /// good to know
    | Note

/// One thing the check found, at a spot on the map when it has one (else -1).
type Finding =
    { Severity: Severity
      Code: string
      Message: string
      X: int
      Y: int }

/// A skill given to the lemming at a tick (where it stood then).
type Assignment = { Tick: int; Skill: Skill; X: int; Y: int }

/// How a route ends.
[<Struct>]
type RouteEnd =
    | Saved
    /// died: the effect (None = fell off the bottom of the map)
    | Died of effect: EffectKind option
    /// walks the same loop for ever
    | Stuck
    | OutOfTime

/// One lemming's run from the hatch.
type Route =
    { Assignments: Assignment list
      End: RouteEnd
      /// ticks from the drop until the end
      Ticks: int
      EndX: int
      EndY: int
      /// where it was each tick (only when recorded)
      Trace: (int * int) list }

type Report =
    { Findings: Finding list
      /// the route with no skills at all
      NoSkills: Route
      /// the plan the search found, if any
      Solution: Route option
      /// the search tried every plan up to MaxSkills (no plan found = none exists
      /// under the search's rules)
      SearchComplete: bool }

/// Plans of up to this many skill uses are searched.
[<Literal>]
let MaxSkills = 3

/// Ticks the search may simulate in all, so a hard level can't hang the editor.
[<Literal>]
let SearchBudget = 20000000

/// Skills the one-lemming search tries. A Blocker only helps a crowd and a
/// Bomber ends its own lemming, so neither moves a lone lemming on.
let private searchSkills = [ Digger; Builder; Basher; Miner; Climber; Floater ]

/// Skills each saved lemming needs for itself (terrain work is done once, for all).
let private perLemming skill = skill = Floater || skill = Climber

let private skillName (s: Skill) = $"%A{s}"

// ─── Simulation ────────────────────────────────────────────────────────────

/// The terrain one lemming sees, with a solid mask of its own to edit.
let private snapshotOf (level: Level) (solid: bool[]) : TerrainSnapshot =
    { Width = level.Width
      Height = level.Height
      Solid = solid
      Steel = level.Steel
      Lava = level.Lava
      Water = level.Water
      Blockers = Set.empty }

/// Ticks the first lemming has before the time runs out.
let private ticksAvailable (level: Level) = max 0 (level.TimeLimitTicks - Game.openDelay)

/// Where and which way a lemming is, as one int (rows -1 .. height; fits for
/// maps up to about 1,000,000 px, e.g. 1200 x 150 leaves room for x 64 x 16).
let private placeKey (level: Level) (s: Lemming.State) =
    (s.X * (level.Height + 2) + s.Y + 1) * 2 + (if s.Dir > 0 then 1 else 0)

let private skillIndex =
    function
    | Walker -> 0 | Digger -> 1 | Blocker -> 2 | Builder -> 3 | Basher -> 4
    | Miner -> 5 | Climber -> 6 | Floater -> 7 | Bomber -> 8

/// Has a lemming on the ground been in this exact state before (since the last
/// terrain edit or skill)? Then it walks a loop for ever. Only checked on the
/// ground: a Floater drifting or a lemming sinking holds still for a tick.
let private looping (level: Level) (seen: HashSet<int>) (s: Lemming.State) =
    s.FallDist = 0
    && s.Submerged = 0
    && not (seen.Add((placeKey level s * 16 + skillIndex s.Skill) * 64 + (s.Counter &&& 63)))

/// Apply a step's terrain edits.
let private apply (level: Level) (terrain: TerrainSnapshot) (mutations: Mutation list) =
    for m in mutations do
        World.applyMutation level.Width level.Height level.Steel terrain.Solid m

/// How a step ended, if it did (after its edits are applied).
let private outcome (level: Level) (terrain: TerrainSnapshot) (s: Lemming.State) (s': Lemming.State) =
    if not s'.Alive then ValueSome(Died(Lemming.deathEffect terrain s s'))
    elif Game.inExitOf level s'.X s'.Y then ValueSome Saved
    else ValueNone

/// Run one lemming from the hatch for up to `limit` ticks. Before each tick
/// `decide tick state` may give it a skill.
let run (level: Level) (limit: int) (decide: int -> Lemming.State -> Skill option) (record: bool) : Route =
    let terrain = snapshotOf level (Array.copy level.Terrain)
    let sx, sy = Game.spawnPoint level
    let seen = HashSet<int>()
    let trace = ResizeArray<int * int>()
    let rec go (s: Lemming.State) t (given: Assignment list) =
        if t >= limit then
            { Assignments = List.rev given; End = OutOfTime; Ticks = t; EndX = s.X; EndY = s.Y; Trace = List.ofSeq trace }
        else
            let s, given =
                match decide t s with
                | Some skill ->
                    seen.Clear()
                    Lemming.withSkill skill s, { Tick = t; Skill = skill; X = s.X; Y = s.Y } :: given
                | None -> s, given
            let s', mutations = Lemming.step terrain s
            apply level terrain mutations
            if record then trace.Add(s'.X, s'.Y)
            let finish e = { Assignments = List.rev given; End = e; Ticks = t + 1; EndX = s'.X; EndY = s'.Y; Trace = List.ofSeq trace }
            match outcome level terrain s s' with
            | ValueSome e -> finish e
            | ValueNone ->
                // the same state again with no terrain change since: a loop
                let edited = not mutations.IsEmpty
                if edited then seen.Clear()
                if not edited && looping level seen s' then finish Stuck
                else go s' (t + 1) given
    go (Lemming.spawnState 0 sx sy) 0 []

/// Run one lemming with the given assignments (by tick) within the time limit.
/// One skill per tick: a later assignment on the same tick replaces an earlier one.
let simulate (level: Level) (assignments: Assignment list) (record: bool) : Route =
    let byTick = assignments |> List.map (fun a -> a.Tick, a.Skill) |> Map.ofList
    run level (ticksAvailable level) (fun t _ -> byTick.TryFind t) record

/// Can a skill be given here? Only to a lemming that isn't busy with one, while
/// it walks (every second pixel) or just as it starts to fall.
let private assignable (terrain: TerrainSnapshot) (s: Lemming.State) =
    s.Alive
    && (s.Skill = Walker || s.Skill = Climber || s.Skill = Floater)
    && ((s.FallDist = 0 && terrain.IsSolid(s.X, s.Y + 1) && s.X % 2 = 0) || s.FallDist = 1)

/// Search for the plan with the fewest skills (up to MaxSkills) that saves the
/// lemming, within SearchBudget ticks. Returns the plan and whether the search
/// ran to the end.
///
/// To stay fast the search tries a skill at a spot only once per skill budget
/// left, from the earliest tick it gets there, even when an earlier skill left
/// the terrain different. So "no plan" means none was found, not a proof.
let solve (level: Level) : Assignment list option * bool =
    let sx, sy = Game.spawnPoint level
    let limit = ticksAvailable level
    let mutable budget = SearchBudget
    let available skill = defaultArg (level.Inventory.TryFind skill) 0
    let skills = searchSkills |> List.indexed |> List.filter (fun (_, k) -> available k > 0)
    // the skills still to give, as far as `depthLeft` more can use them: one
    // visited table per such budget, keyed by spot + skill to try, holding the
    // earliest tick tried
    let visited = Dictionary<int, Dictionary<int, int>>()
    let tableFor (plan: Assignment list) depthLeft =
        let group =
            searchSkills
            |> List.fold (fun acc k ->
                let left = available k - (plan |> List.filter (fun a -> a.Skill = k) |> List.length)
                acc * 4 + max 0 (min left depthLeft)) depthLeft
        match visited.TryGetValue group with
        | true, table -> table
        | _ ->
            let table = Dictionary<int, int>()
            visited.[group] <- table
            table
    let firstVisit (table: Dictionary<int, int>) key t =
        match table.TryGetValue key with
        | true, t0 when t0 <= t -> false
        | _ ->
            table.[key] <- t
            true
    // `owned`: this branch has its own copy of the terrain (it copies on its first edit)
    let rec explore (s: Lemming.State) (terrain: TerrainSnapshot) (owned: bool) t (plan: Assignment list) depthLeft =
        let seen = HashSet<int>()
        let table = if depthLeft > 0 then tableFor plan depthLeft else null
        let lastTick = match plan with a :: _ -> a.Tick | [] -> -1
        let rec go (s: Lemming.State) (terrain: TerrainSnapshot) owned t =
            if t >= limit || budget <= 0 then None
            else
                budget <- budget - 1
                // try each skill here first (a second skill on the same tick
                // only replaces the first, so it's never tried)
                let branch =
                    if depthLeft > 0 && t > lastTick && assignable terrain s then
                        let place = placeKey level s * 16 + skillIndex s.Skill
                        skills
                        |> List.tryPick (fun (i, skill) ->
                            let used = plan |> List.filter (fun a -> a.Skill = skill) |> List.length
                            if used >= available skill || s.Skill = skill then None
                            elif not (firstVisit table (place * 8 + i) t) then None
                            else
                                let a = { Tick = t; Skill = skill; X = s.X; Y = s.Y }
                                explore (Lemming.withSkill skill s) terrain false t (a :: plan) (depthLeft - 1))
                    else None
                match branch with
                | Some p -> Some p
                | None ->
                    let s', mutations = Lemming.step terrain s
                    let edited = not mutations.IsEmpty
                    let terrain, owned =
                        if edited && not owned then snapshotOf level (Array.copy terrain.Solid), true
                        else terrain, owned
                    apply level terrain mutations
                    match outcome level terrain s s' with
                    | ValueSome Saved -> Some(List.rev plan)
                    | ValueSome _ -> None
                    | ValueNone ->
                        if edited then seen.Clear()
                        if not edited && looping level seen s' then None
                        else go s' terrain owned (t + 1)
        go s terrain owned t
    let rec deepen depth =
        if depth > MaxSkills then None, budget > 0
        else
            visited.Clear()
            match explore (Lemming.spawnState 0 sx sy) (snapshotOf level level.Terrain) false 0 [] depth with
            | Some plan -> Some plan, true
            | None when budget <= 0 -> None, false
            | None -> deepen (depth + 1)
    deepen 0

// ─── The check ─────────────────────────────────────────────────────────────

let private describeEnd (r: Route) =
    match r.End with
    | Saved -> "reach the exit"
    | Died(Some Splat) -> $"splat at (%d{r.EndX}, %d{r.EndY})"
    | Died(Some Burn) -> $"burn in lava at (%d{r.EndX}, %d{r.EndY})"
    | Died(Some Drown) -> $"drown at (%d{r.EndX}, %d{r.EndY})"
    | Died(Some Explode) -> $"explode at (%d{r.EndX}, %d{r.EndY})"
    | Died None -> $"fall off the map at x %d{r.EndX}"
    | Stuck -> $"walk back and forth for ever around (%d{r.EndX}, %d{r.EndY})"
    | OutOfTime -> $"run out of time at (%d{r.EndX}, %d{r.EndY})"

let private describePlan (plan: Assignment list) =
    plan |> List.map (fun a -> sprintf "%s at (%d, %d)" (skillName a.Skill) a.X a.Y) |> String.concat ", then "

let private allSkills = [| Walker; Digger; Blocker; Builder; Basher; Miner; Climber; Floater; Bomber |]

/// A plan as flat ints (tick, skill, x, y per assignment), to pass to and from a Web Worker.
let planToInts (plan: Assignment list) : int[] =
    plan |> List.collect (fun a -> [ a.Tick; skillIndex a.Skill; a.X; a.Y ]) |> Array.ofList

let planOfInts (ints: int[]) : Assignment list =
    List.init (ints.Length / 4) (fun i ->
        { Tick = ints.[4 * i]; Skill = allSkills.[ints.[4 * i + 1]]; X = ints.[4 * i + 2]; Y = ints.[4 * i + 3] })

/// Check a level with the given solution search (`solve`, or its result from
/// elsewhere, e.g. the editor's Web Worker): hatch, exit, numbers, the no-skill
/// walk and a solution.
let checkWith (search: Level -> Assignment list option * bool) (level: Level) : Report =
    let w, h = level.Width, level.Height
    let solidAt x y = x >= 0 && x < w && y >= 0 && y < h && level.Terrain.[y * w + x]
    let hazardAt x y = x >= 0 && x < w && y >= 0 && y < h && (level.Lava.[y * w + x] || level.Water.[y * w + x])
    let findings = ResizeArray<Finding>()
    let add sev code x y msg = findings.Add { Severity = sev; Code = code; Message = msg; X = x; Y = y }
    let floaters = defaultArg (level.Inventory.TryFind Floater) 0

    // numbers
    if level.SaveTarget < 1 then add Problem "save-target" -1 -1 "The save target is 0: the level is won before it starts."
    if level.SaveTarget > level.SpawnCount then
        add Problem "save-target" -1 -1 $"The save target (%d{level.SaveTarget}) is more than the lemmings released (%d{level.SpawnCount})."
    if level.SpawnEveryTicks < 1 then add Problem "spawn-rate" -1 -1 "Lemmings must be released at least 1 tick apart."
    if level.TimeLimitTicks <= Game.openDelay then
        add Problem "time-limit" -1 -1 $"The time limit (%d{level.TimeLimitTicks} ticks) runs out before the hatch opens (%d{Game.openDelay})."

    // the hatch: where lemmings appear, and the drop below it
    let sx, sy = Game.spawnPoint level
    if sx < 0 || sx >= w || sy < 0 || sy >= h then
        add Problem "hatch-off-map" sx sy "The hatch is outside the map."
    elif solidAt sx sy then
        add Problem "hatch-in-terrain" sx sy "Lemmings appear inside terrain: move the hatch into open air."
    else
        match [ sy + 1 .. h - 1 ] |> List.tryFind (fun y -> solidAt sx y) with
        | None -> add Problem "hatch-no-ground" sx sy "There is no ground below the hatch: lemmings fall off the map."
        | Some ground ->
            let drop = ground - 1 - sy
            if hazardAt sx (ground - 1) then
                add Problem "hatch-into-hazard" sx (ground - 1) "Lemmings drop from the hatch straight into lava or water."
            elif drop > Lemming.safeFall then
                if floaters = 0 then
                    add Problem "hatch-drop" sx ground $"The drop from the hatch is %d{drop} px: every lemming splats (safe is %d{Lemming.safeFall}) and there are no Floaters."
                elif floaters < level.SaveTarget then
                    add Problem "hatch-drop" sx ground $"The drop from the hatch is %d{drop} px (safe is %d{Lemming.safeFall}): each saved lemming needs a Floater, but there are %d{floaters} for %d{level.SaveTarget}."
                else
                    add Warning "hatch-drop" sx ground $"The drop from the hatch is %d{drop} px (safe is %d{Lemming.safeFall}): every lemming needs a Floater straight away."

    // the exit: lemmings are saved anywhere inside it, so it needs one free cell
    let e = level.Exit
    let exitCells = [ for y in e.Y .. e.Y + e.H - 1 do for x in e.X .. e.X + e.W - 1 do yield x, y ]
    let inMap = exitCells |> List.filter (fun (x, y) -> x >= 0 && x < w && y >= 0 && y < h)
    if inMap.IsEmpty then add Problem "exit-off-map" e.X e.Y "The exit is outside the map."
    else
        let free = inMap |> List.filter (fun (x, y) -> not (solidAt x y))
        if free.IsEmpty then add Problem "exit-buried" (e.X + e.W / 2) (e.Y + e.H / 2) "The exit is buried in terrain: no lemming can get inside it."
        elif free |> List.forall (fun (x, y) -> hazardAt x y) then
            add Problem "exit-in-hazard" (e.X + e.W / 2) (e.Y + e.H / 2) "The exit is all lava or water: lemmings die before they're saved."

    // the walk with no skills
    let noSkills = simulate level [] true
    match noSkills.End with
    | Saved -> add Warning "no-skills-needed" noSkills.EndX noSkills.EndY "Lemmings reach the exit without any skills: the level is not a puzzle."
    | _ -> add Note "no-skill-walk" noSkills.EndX noSkills.EndY ("With no skills, lemmings " + describeEnd noSkills + ".")

    // a solution, unless walking already solves it
    let solution, complete =
        match noSkills.End with
        | Saved -> Some noSkills, true
        | _ ->
            match search level with
            | Some plan, c -> Some(simulate level plan true), c
            | None, c -> None, c
    match solution with
    | Some r when not r.Assignments.IsEmpty ->
        add Note "solvable" -1 -1 (sprintf "Solvable: %s; the lemming is saved after %d ticks." (describePlan r.Assignments) r.Ticks)
        // the last lemming the target needs leaves the hatch this late
        let lastOut = Game.openDelay + (level.SaveTarget - 1) * max 1 level.SpawnEveryTicks
        if lastOut + r.Ticks > level.TimeLimitTicks then
            add Warning "time-tight" -1 -1 (sprintf "Time is tight: the %d-th lemming would arrive after about %d ticks, the limit is %d." level.SaveTarget (lastOut + r.Ticks) level.TimeLimitTicks)
        for skill in r.Assignments |> List.map (fun a -> a.Skill) |> List.distinct |> List.filter perLemming do
            let need = level.SaveTarget * (r.Assignments |> List.filter (fun a -> a.Skill = skill) |> List.length)
            let have = defaultArg (level.Inventory.TryFind skill) 0
            if have < need then
                add Warning "skills-short" -1 -1 (sprintf "This plan needs a %s for every saved lemming: %d, but there are %d." (skillName skill) need have)
    | Some _ -> ()
    | None when complete ->
        add Problem "unsolvable" -1 -1 $"The search found no plan of up to %d{MaxSkills} skills that saves a lemming: the level is probably impossible."
    | None ->
        add Warning "search-limit" -1 -1 $"No plan found within the search limit (up to %d{MaxSkills} skills); the level may still be solvable."

    let order = function Problem -> 0 | Warning -> 1 | Note -> 2
    { Findings = findings |> Seq.sortBy (fun f -> order f.Severity) |> List.ofSeq
      NoSkills = noSkills
      Solution = solution
      SearchComplete = complete }

/// Check a level: hatch, exit, numbers, the no-skill walk and a solution.
let check (level: Level) : Report = checkWith solve level
