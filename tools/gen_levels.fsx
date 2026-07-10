// Author the campaign and emit src/levels.json (consumed by src/Levels.fs).
// Levels are data: difficulty is numbers + terrain shapes, never engine code.
// Terrain is run-length encoded (runs of empty/solid, starting with empty) to
// keep the file small. Levels may be wider than the 240px viewport (the game
// scrolls); height is a fixed 150.
//
// Run with:  dotnet fsi tools/gen_levels.fsx

open System.IO
open System.Text.Json
open System.Text.RegularExpressions

let H = 150

let private bitmap width (rects: (int * int * int * int) list) =
    let solid = Array.zeroCreate (width * H)
    for (x0, y0, w, h) in rects do
        for y in y0 .. y0 + h - 1 do
            for x in x0 .. x0 + w - 1 do
                if x >= 0 && x < width && y >= 0 && y < H then
                    solid.[y * width + x] <- true
    solid

/// RLE: alternating run lengths, starting with a run of EMPTY (false) cells.
let private rle (cells: bool[]) =
    let runs = ResizeArray<int>()
    let mutable cur = false
    let mutable count = 0
    for c in cells do
        if c = cur then
            count <- count + 1
        else
            runs.Add count
            cur <- c
            count <- 1
    runs.Add count
    runs.ToArray()

let private region x y w h = {| x = x; y = y; w = w; h = h |}
let private inv pairs = pairs |> List.map (fun (s, n) -> {| skill = s; count = n |})
let private groundY = 126
let private groundOf width = (0, groundY, width, 24)

let private hazard kind x y w h = {| x = x; y = y; w = w; h = h; kind = kind |}

// A tiny explicit-state LCG (no System.Random: the same level name must yield
// the same map on every generator run).
let private lcg (s: uint) = s * 1664525u + 1013904223u

/// One roll in 0 .. max-1, plus the advanced generator state.
let private rand max s =
    let s = lcg s
    int (s >>> 16) % max, s

/// Deterministic (x, roll) spots along [x0, limit): each spot takes a roll in
/// 0 .. rollMax-1 and advances by a seeded gap. Drives both relief mounds and
/// scenery placement.
let private spots seed x0 limit minGap gapSpread rollMax =
    (seed, x0)
    |> List.unfold (fun (s, x) ->
        if x >= limit then
            None
        else
            let roll, s = rand rollMax s
            let gap, s = rand gapSpread s
            Some((x, roll), (s, x + roll + minGap + gap)))

// Per-level cosmetic switches, stated explicitly on every campaign entry:
// whether free-standing bumps are dressed as pillars, and whether ceiling
// masses hang from the sky.
let private level (opts: {| pillars: bool; ceiling: bool |}) name theme width rects (hatch: {| x: int; y: int; w: int; h: int |}) (exit: {| x: int; y: int; w: int; h: int |}) (hazards: {| x: int; y: int; w: int; h: int; kind: string |} list) spawnCount spawnEvery saveTarget timeLimit inventory =
    let size = width * H
    let solid = bitmap width rects // dirt
    let steel: bool[] = Array.zeroCreate size
    let lava: bool[] = Array.zeroCreate size
    let water: bool[] = Array.zeroCreate size

    let fill (mask: bool[]) x0 y0 w h v =
        for y in y0 .. y0 + h - 1 do
            for x in x0 .. x0 + w - 1 do
                if x >= 0 && x < width && y >= 0 && y < H then
                    mask.[y * width + x] <- v
    // Steel is solid + indestructible; lava/water are carved into a pit below ground.
    for hz in hazards do
        match hz.kind with
        | "steel" ->
            fill steel hz.x hz.y hz.w hz.h true
            fill solid hz.x hz.y hz.w hz.h true
        | "lava" ->
            fill lava hz.x hz.y hz.w hz.h true
            fill solid hz.x hz.y hz.w hz.h false
        | "water" ->
            fill water hz.x hz.y hz.w hz.h true
            fill solid hz.x hz.y hz.w hz.h false
        | other -> failwithf "unknown hazard kind '%s'" other

    // ---- Relief & scenery (cosmetics; seeded by name so runs are reproducible) --
    let seed = lcg (uint (name |> Seq.sumBy int))

    /// Topmost solid row of a column, if the column has any ground.
    let surfaceAt x =
        [ 0 .. H - 1 ] |> List.tryFind (fun y -> solid.[y * width + x])

    let profile = Array.init width surfaceAt

    // Stretches that must stay exactly as authored: the hatch drop, the exit
    // approach, hazard lips, and anywhere the surface already jumps by more
    // than a step (wall bases, ledge/plateau edges, gap rims) — i.e. all the
    // places skills get assigned and routes are measured.
    let keepFlat =
        [ hatch.x - 10, hatch.x + hatch.w + 10
          exit.x - 10, exit.x + exit.w + 10
          for hz in hazards do
              hz.x - 16, hz.x + hz.w + 16
          for x in 0 .. width - 2 do
              match profile.[x], profile.[x + 1] with
              | Some a, Some b when abs (a - b) <= 3 -> ()
              | _ -> x - 16, x + 17 ]

    let isFlat x =
        keepFlat |> List.exists (fun (a, b) -> x >= a && x < b)

    // Gentle mounds (1-2px tall, in 1px steps) on the remaining level stretches.
    // A walker auto-steps 3px, so these only make the march bob up and down —
    // routes are unchanged (every level is still verified by tools/solver.fsx).
    let moundBase x w =
        match if x < width then profile.[x] else None with
        | Some top when
            [ x - 2 .. x + w + 1 ]
            |> List.forall (fun c ->
                c >= 0
                && c < width
                && not (isFlat c)
                && profile.[c] = Some top // level base only — never near an edge
                && not steel.[top * width + c])
            ->
            Some top
        | _ -> None

    for x, roll in spots seed 8 (width - 24) 24 40 14 do
        let w = 8 + roll

        match moundBase x w with
        | Some top ->
            fill solid x (top - 1) w 1 true
            if w > 8 then
                fill solid (x + 2) (top - 2) (w - 4) 1 true
        | None -> ()

    let nearRegion (r: {| x: int; y: int; w: int; h: int |}) pad x = x >= r.x - pad && x < r.x + r.w + pad

    // Ground shape frozen after the relief pass: the ceiling clearance test and
    // scenery placement must both see the ground (incl. mound tops), never a
    // ceiling cell that a later pass hangs from the sky.
    let groundProfile = Array.init width surfaceAt

    // ---- Ceiling masses: organic rock hanging from the top edge, in the spirit
    // of the original game's cave roofs. Cosmetic only: a column is drawn only
    // where everything nearby is far below it and away from the hatch, so no
    // route (walk, build, climb) can reach it — solver.fsx re-verifies anyway.
    let ceilingOk x bottom =
        not (nearRegion hatch 14 x)
        && [ max 0 (x - 8) .. min (width - 1) (x + 8) ]
           |> List.forall (fun c ->
               match groundProfile.[c] with
               | Some top -> top >= bottom + 45
               | None -> true)

    for x0, roll in (if opts.ceiling then spots (lcg (lcg seed)) 4 (width - 30) 60 80 30 else []) do
        let w = 30 + roll // 30..59 px wide
        let mutable s = lcg (uint (x0 * 7 + roll))
        let mutable depth = 6

        for x in x0 .. min (width - 1) (x0 + w - 1) do
            // A random walk (-2..+2 per column) gives the bumpy lower edge; an
            // occasional drip becomes a short stalactite. Ends taper so the
            // mass fades out instead of stopping at a cliff.
            let step, s2 = rand 5 s
            let drip, s3 = rand 12 s2
            s <- s3
            depth <- max 4 (min 20 (depth + step - 2))
            let d = if drip = 0 then depth + 5 else depth
            let d = min d (4 + 2 * min (x - x0) (x0 + w - 1 - x))
            if ceilingOk x d then fill solid x 0 1 d true

    // Scenery spots: just (x, ground row, variant). The THEME decides at render
    // time what a variant looks like (grass tuft vs mushroom vs torch), so
    // re-theming a level re-skins its props and the terrain stays pure data.

    let decor =
        spots (lcg seed) 10 (width - 12) 22 38 4
        |> List.choose (fun (x, v) ->
            if nearRegion hatch 8 x || nearRegion exit 8 x then
                None
            else // ground profile, not `profile`: props stand on mound tops, never on a ceiling
                groundProfile.[x] |> Option.map (fun y -> {| x = x; y = y; v = v |}))

    // Free-standing blocks that rise from the ground slab (the bashable "bumps")
    // read as columns: mark them so the renderer dresses them as fluted
    // roman/egyptian pillars. Cosmetic only — collision is still the solid mask,
    // so bashing a tunnel through one carves the fluting with it.
    let pillars =
        if opts.pillars then
            rects
            |> List.filter (fun (_, y, w, h) -> y + h = groundY && w <= 24 && h <= 40)
            |> List.map (fun (x, y, w, h) -> region x y w h)
        else
            []

    {| name = name
       theme = theme
       width = width
       height = H
       terrain = rle solid
       steel = rle steel
       lava = rle lava
       water = rle water
       hatch = hatch
       exit = exit
       decor = decor
       pillars = pillars
       spawnCount = spawnCount
       spawnEveryTicks = spawnEvery
       saveTarget = saveTarget
       timeLimitTicks = timeLimit
       inventory = inventory |}

let private hatch = region 16 108 8 4
let private exit = region 212 116 16 10
let private noHaz = []

// Roughly increasing difficulty; each early level introduces one tool, then the
// later ones combine them, ending with hazards. Themes vary for visual variety.
let campaign =
    [ // Each puzzle needs at least TWO tools, used in the right place/order. Engine
      // rule that shapes the design: Climber/Floater are permanent until you click
      // another skill over them, and an active skill (bash/dig/mine/build) must come
      // BEFORE a permanent one on the same lemming (the active one reverts to Walker
      // when done; a permanent one never does). All routes verified by tools/solver.fsx.
      // 1 — bash the wall on the high ledge, then float off the cliff to the exit.
      level {| pillars = true; ceiling = true |} "1. Bash and bail" "earth" 280 [ groundOf 280; (0, 90, 180, 8); (110, 68, 18, 30) ] (region 16 74 8 4) (region 220 116 16 10) noHaz 10 14 7 2000 (inv [ "Basher", 3; "Floater", 8; "Blocker", 2; "Bomber", 1 ])
      // 2 — bash through the wall, then build a ramp up onto the exit ledge.
      level {| pillars = true; ceiling = false |} "2. Break and build" "stone" 280 [ groundOf 280; (110, 96, 18, 30); (220, 114, 60, 12) ] hatch (region 244 104 16 12) noHaz 12 14 9 2200 (inv [ "Basher", 3; "Builder", 4; "Blocker", 2; "Bomber", 1 ])
      // 3 — dig down through the slab to the ground, then bash to the exit.
      level {| pillars = true; ceiling = true |} "3. Dig and breach" "earth" 260 [ groundOf 260; (0, 110, 200, 8); (200, 60, 18, 89) ] (region 16 92 8 4) (region 230 116 16 10) noHaz 12 14 8 2500 (inv [ "Digger", 3; "Basher", 3; "Blocker", 2; "Bomber", 1 ])
      // 4 — mine diagonally down off the plateau, then build onto the exit ledge.
      level {| pillars = true; ceiling = true |} "4. Mine and build" "stone" 260 [ groundOf 260; (0, 60, 120, 90); (200, 114, 40, 12) ] (region 16 42 8 4) (region 220 104 16 12) noHaz 12 14 8 2500 (inv [ "Miner", 3; "Builder", 4; "Blocker", 2; "Bomber", 1 ])
      // 5 — build a bridge over the lava, then climb the wall to the high exit.
      level {| pillars = true; ceiling = true |} "5. Fire and the wall" "brick" 280 [ groundOf 280; (180, 40, 16, 86) ] hatch (region 180 30 16 10) [ hazard "lava" 100 126 12 24 ] 10 14 6 2200 (inv [ "Builder", 4; "Climber", 6; "Blocker", 2; "Bomber", 1 ])
      // 6 — build over the gap, then bash through the wall to the exit.
      level {| pillars = true; ceiling = true |} "6. Bridge and breach" "stone" 280 [ (0, 126, 124, 24); (136, 126, 144, 24); (190, 96, 18, 30) ] hatch (region 250 116 16 10) noHaz 12 14 8 2500 (inv [ "Builder", 4; "Basher", 3; "Blocker", 2; "Bomber", 1 ])
      // 7 — bash through the first wall, then climb the tall one to the exit on top.
      level {| pillars = false; ceiling = true |} "7. Break and scale" "brick" 280 [ groundOf 280; (90, 96, 18, 30); (180, 40, 16, 86) ] hatch (region 180 30 16 10) noHaz 10 14 6 2200 (inv [ "Basher", 3; "Climber", 6; "Blocker", 2; "Bomber", 1 ])
      // 8 — mine down off the plateau, then bash through the wall to the exit.
      level {| pillars = true; ceiling = true |} "8. Mine and breach" "earth" 260 [ groundOf 260; (0, 60, 120, 90); (190, 96, 18, 30) ] (region 16 42 8 4) (region 240 116 16 10) noHaz 12 14 8 2600 (inv [ "Miner", 3; "Basher", 3; "Blocker", 2; "Bomber", 1 ])
      // 9 — everything unlocked: get off the plateau (mine/float), bash the wall,
      // bridge the lava. Three tools, generous supply.
      level {| pillars = true; ceiling = true |} "9. Anything goes" "hell" 600 [ groundOf 600; (0, 60, 150, 90); (320, 96, 18, 30) ] (region 16 42 8 4) (region 560 116 16 10) [ hazard "lava" 430 126 12 24 ] 14 12 8 3200 (inv [ "Miner", 4; "Basher", 4; "Builder", 6; "Floater", 8; "Digger", 3; "Climber", 4; "Blocker", 3; "Bomber", 2 ])
      // 10 — bridge the lava AND the water; a steel patch is walkable but can't be
      // dug through (try it).
      level {| pillars = true; ceiling = true |} "10. Inferno" "hell" 360 [ groundOf 360 ] hatch (region 332 116 16 10) [ hazard "steel" 60 126 40 24; hazard "lava" 120 126 12 24; hazard "water" 210 126 12 24 ] 10 16 4 2800 (inv [ "Builder", 12; "Floater", 6; "Digger", 3; "Blocker", 4; "Bomber", 2 ]) ]

// Design rule: a level offering Blockers must also offer Bombers (to free a
// trapped blocker), and its save target must leave margin for those sacrifices.
campaign
|> List.iter (fun lvl ->
    let has s = lvl.inventory |> List.exists (fun p -> p.skill = s)
    if has "Blocker" && not (has "Bomber") then
        failwithf "Level '%s' offers Blocker but no Bomber" lvl.name)

// Readable output: indented layout, but with leaf objects (regions, decor and
// inventory entries) and number arrays (the RLE runs) collapsed back onto
// single lines — one meaningful thing per line, not one token per line.
let json =
    let raw = JsonSerializer.Serialize(campaign, JsonSerializerOptions(WriteIndented = true))
    let collapse (m: Match) = Regex.Replace(m.Value, @"\s+", " ")
    let leafObjs = Regex.Replace(raw, @"\{[^{}\[\]]*\}", collapse)
    Regex.Replace(leafObjs, @"\[[\s\d,-]*\]", collapse)

let out = Path.Combine(__SOURCE_DIRECTORY__, "..", "src", "levels.json")
File.WriteAllText(out, json)
printfn "Wrote %s  (%d levels)" (Path.GetFullPath out) (List.length campaign)
