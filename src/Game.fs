module FsLemming.Game

open FsLemming.Types
open FsLemming.World
open FsLemming.Lemming
open FsLemming.Inventory

/// The coordinator / level runner. It owns the World, the skill Inventory, and
/// the live set of lemmings, and drives the deterministic two-phase tick. It
/// holds run state (spawned/saved/dead/elapsed) but no domain logic of its own —
/// every decision is delegated to an actor via messages.
/// Ticks the hatch spends opening before the first lemming drops — gives the
/// player a moment to read the level.
let private openDelay = 40

type Game(level: Level) =

    let world = World(level.Width, level.Height, level.Terrain, level.Steel, level.Lava, level.Water)
    let inventory = Inventory(level.Inventory)

    // The live lemmings, plus run bookkeeping. Mutable because the population
    // changes over time (the hatch adds; exit/death remove).
    let mutable active: Lemming list = []
    let mutable nextId = 0
    let mutable spawned = 0
    let mutable saved = 0
    let mutable dead = 0
    let mutable elapsed = 0

    let inExit (v: LemmingView) =
        v.X >= level.Exit.X
        && v.X < level.Exit.X + level.Exit.W
        && v.Y >= level.Exit.Y
        && v.Y < level.Exit.Y + level.Exit.H

    let outcome () =
        if saved >= level.SaveTarget then Won
        elif elapsed >= level.TimeLimitTicks then Lost
        // Everyone has been released and none are left walking, yet short of target.
        elif spawned >= level.SpawnCount && List.isEmpty active then Lost
        else Playing

    member _.Level = level
    member _.Inventory = inventory

    /// Try to spend one `skill` from the inventory and, if granted, assign it to
    /// lemming `id`. Returns whether one was available.
    member _.TryAssign(id, skill) =
        async {
            // Find the lemming first, THEN spend — so we never waste a unit on one
            // that was retired between the click and now.
            match active |> List.tryFind (fun l -> l.Id = id) with
            | Some l ->
                let! (curSkill, curFuse) = l.Current
                // Don't waste a unit re-assigning the role it already has: the same
                // skill, or arming a Bomber that's already counting down. (A *different*
                // skill still overrides, e.g. Basher over Floater.)
                let redundant =
                    match skill with
                    | Bomber -> curFuse > 0
                    | s -> curSkill = s

                if redundant then
                    return false
                else
                    let! granted = inventory.TryTake skill
                    // The lemming may have retired (exited/died) while the take
                    // was in flight — its agent is stopped and would silently
                    // drop the SetSkill. Re-check and refund rather than burn a
                    // unit on a ghost. (Retirement removes it from `active` and
                    // posts Stop in one step, and this check + SetSkill run in
                    // one step too, so if it's still here the SetSkill message
                    // is queued ahead of any future Stop.)
                    if granted then
                        if active |> List.exists (fun l2 -> l2.Id = id) then
                            l.SetSkill skill
                            return true
                        else
                            inventory.Refund skill
                            return false
                    else
                        return false
            | None -> return false
        }

    /// Nuke: detonate every active lemming with a staggered fuse (a fan-out of
    /// individual messages, not a global flag), and write off everyone still in
    /// the hatch — the hatch stops and the unspawned count as lost.
    member _.NukeAll() =
        active |> List.iteri (fun i l -> l.Detonate(10 + i * 2))
        dead <- dead + (level.SpawnCount - spawned)
        spawned <- level.SpawnCount

    /// Stop every live lemming's agent (e.g. when leaving the level).
    member _.StopAll() = active |> List.iter (fun l -> l.Stop())

    /// One simulation step. See docs/design.md for the two-phase tick rationale.
    member _.Tick() =
        async {
            // 0. SPAWN from the hatch — but only after the gate-opening delay, and
            //    then on schedule until the quota is reached.
            if outcome () = Playing
               && spawned < level.SpawnCount
               && elapsed >= openDelay
               // max 1: a zero interval (hand-edited/buggy level data) would be
               // `x % 0` = NaN under Fable — no lemming would ever spawn.
               && (elapsed - openDelay) % max 1 level.SpawnEveryTicks = 0 then
                // Prepend (O(1)); order among lemmings doesn't matter, they're independent.
                active <- Lemming(nextId, level.Hatch.X + level.Hatch.W / 2, level.Hatch.Y) :: active
                nextId <- nextId + 1
                spawned <- spawned + 1

            // 1. SENSE — every lemming reacts to the same start-of-tick snapshot.
            let! terrain = world.Snapshot()
            let! results = active |> List.map (fun l -> l.Tick terrain) |> Async.Parallel
            let resultList = List.ofArray results
            let views = resultList |> List.map (fun r -> r.View)

            // 2. COMMIT terrain edits and the new blocker positions.
            world.Apply(resultList |> List.collect (fun r -> r.Mutations))

            world.SetBlockers(
                views
                |> List.filter (fun v -> v.Alive && v.Skill = Blocker)
                |> List.map (fun v -> v.X, v.Y)
                |> Set.ofList
            )

            // 3. Retire lemmings: those who reached the exit are saved, the dead
            //    are counted, the rest stay active. (views is in `active` order.)
            //    Retired lemmings' agents are stopped (graceful shutdown).
            let zipped = List.zip active views
            let isSaved (v: LemmingView) = v.Alive && inExit v
            let isRetired (v: LemmingView) = not v.Alive || inExit v
            saved <- saved + (zipped |> List.filter (fun (_, v) -> isSaved v) |> List.length)
            dead <- dead + (zipped |> List.filter (fun (_, v) -> not v.Alive) |> List.length)
            zipped |> List.iter (fun (l, v) -> if isRetired v then l.Stop())
            active <- zipped |> List.choose (fun (l, v) -> if isRetired v then None else Some l)

            // The clock only runs while the level is still in play — once it's
            // won/lost (the last lemming reached the exit, or all are accounted
            // for), time freezes.
            if outcome () = Playing then
                elapsed <- elapsed + 1

            // Where lemmings died this tick + which effect to show (explosion crater
            // itself is already in the mutations above).
            let effects =
                resultList
                |> List.choose (fun r -> r.Effect |> Option.map (fun fx -> r.View.X, r.View.Y, fx))

            let status =
                { Spawned = spawned
                  Saved = saved
                  Dead = dead
                  Active = List.length active
                  Elapsed = elapsed
                  OpenProgress = min 1.0 (float elapsed / float openDelay)
                  Outcome = outcome () }

            return terrain, views, effects, status
        }
