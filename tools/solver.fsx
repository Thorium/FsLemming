// Campaign regression: replays ONE lemming through a scripted plan of skill
// assignments per campaign level and reports whether it reaches the exit. If a
// single lemming can solve the route, the crowd can too (with blockers/timing).
//
// It verifies the ACTUAL shipped data (src/levels.json, the output of
// gen_levels.fsx, surface relief included) with the game's own rules:
// LevelFile.fsx loads src/Lemming.fs and src/World.fs, and the run goes through
// src/LevelCheck.fs — the same code as the editor's Check button and
// tools/levels.fsx. Regenerate levels, then re-run this.
//
// (tools/levels.fsx --solve finds a plan for any level by itself; these
// scripted plans pin down the routes each level was designed around.)
//
// Run with:  dotnet fsi tools/solver.fsx

#load "LevelFile.fsx"

open FsLemming.Types
open FsLemming.LevelCheck
open LevelFile

/// A plan item fires when the WALKER reaches triggerX in its travel direction —
/// modelling the player clicking once the lemming is in position.
let byTriggerX (plan: (int * Skill) list) =
    let mutable pending = plan
    fun (_: int) (s: FsLemming.Lemming.State) ->
        match pending with
        | (tx, skill) :: rest when s.Skill = Walker && ((s.Dir > 0 && s.X >= tx) || (s.Dir < 0 && s.X <= tx)) ->
            pending <- rest
            Some skill
        | _ -> None

// One scripted route per campaign level, in order (triggerX, skill).
let plans =
    [ [ 90, Basher; 160, Floater ]          // 1 bash the ledge wall, float off the cliff
      [ 100, Basher; 206, Builder ]         // 2 bash the wall, build onto the exit ledge
      [ 100, Digger; 190, Basher ]          // 3 dig through the slab, bash to the exit
      [ 60, Miner; 190, Builder ]           // 4 mine off the plateau, build onto the ledge
      [ 99, Builder; 170, Climber ]         // 5 bridge the lava, climb the wall
      [ 123, Builder; 180, Basher ]         // 6 bridge the gap, bash the wall
      [ 80, Basher; 165, Climber ]          // 7 bash the wall, climb the tall one
      [ 60, Miner; 180, Basher ]            // 8 mine off the plateau, bash the wall
      [ 90, Miner; 310, Basher; 429, Builder ] // 9 mine, bash, bridge the lava
      [ 119, Builder; 209, Builder ] ]      // 10 bridge the lava AND the water

let levels = loadLevels campaignPath

if List.length levels <> List.length plans then
    failwithf "levels.json has %d levels but solver has %d plans" (List.length levels) (List.length plans)

printfn "Campaign solvability (from src/levels.json):"

let results =
    [ for level, plan in List.zip levels plans do
        let r = run level 9000 (byTriggerX plan) false
        let result =
            match r.End with
            | Saved -> "SOLVED"
            | Died _ -> $"DIED (%d{r.EndX},%d{r.EndY})"
            | Stuck -> $"STUCK (%d{r.EndX},%d{r.EndY})"
            | OutOfTime -> "TIMEOUT"
        printfn "  %-24s %s" level.Name result
        yield r.End = Saved ]

if results |> List.contains false then exit 1
