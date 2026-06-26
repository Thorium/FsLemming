// Author the campaign and emit src/levels.json (consumed by src/Levels.fs).
// Levels are data: difficulty is numbers + terrain shapes, never engine code.
// Terrain is run-length encoded (runs of empty/solid, starting with empty) to
// keep the file small. Levels may be wider than the 240px viewport (the game
// scrolls); height is a fixed 150.
//
// Run with:  dotnet fsi tools/gen_levels.fsx

open System.IO
open System.Text.Json

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
let private groundOf width = (0, 126, width, 24)

let private hazard kind x y w h = {| x = x; y = y; w = w; h = h; kind = kind |}

let private level name theme width rects hatch exit (hazards: {| x: int; y: int; w: int; h: int; kind: string |} list) spawnCount spawnEvery saveTarget timeLimit inventory =
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
      level "1. Bash and bail" "earth" 280 [ groundOf 280; (0, 90, 180, 8); (110, 68, 18, 30) ] (region 16 74 8 4) (region 220 116 16 10) noHaz 10 14 7 2000 (inv [ "Basher", 3; "Floater", 8; "Blocker", 2; "Bomber", 1 ])
      // 2 — bash through the wall, then build a ramp up onto the exit ledge.
      level "2. Break and build" "stone" 280 [ groundOf 280; (110, 96, 18, 30); (220, 114, 60, 12) ] hatch (region 244 104 16 12) noHaz 12 14 9 2200 (inv [ "Basher", 3; "Builder", 4; "Blocker", 2; "Bomber", 1 ])
      // 3 — dig down through the slab to the ground, then bash to the exit.
      level "3. Dig and breach" "earth" 260 [ groundOf 260; (0, 110, 200, 8); (200, 60, 18, 89) ] (region 16 92 8 4) (region 230 116 16 10) noHaz 12 14 8 2500 (inv [ "Digger", 3; "Basher", 3; "Blocker", 2; "Bomber", 1 ])
      // 4 — mine diagonally down off the plateau, then build onto the exit ledge.
      level "4. Mine and build" "stone" 260 [ groundOf 260; (0, 60, 120, 90); (200, 114, 40, 12) ] (region 16 42 8 4) (region 220 104 16 12) noHaz 12 14 8 2500 (inv [ "Miner", 3; "Builder", 4; "Blocker", 2; "Bomber", 1 ])
      // 5 — build a bridge over the lava, then climb the wall to the high exit.
      level "5. Fire and the wall" "brick" 280 [ groundOf 280; (180, 40, 16, 86) ] hatch (region 180 30 16 10) [ hazard "lava" 100 126 12 24 ] 10 14 6 2200 (inv [ "Builder", 4; "Climber", 6; "Blocker", 2; "Bomber", 1 ])
      // 6 — build over the gap, then bash through the wall to the exit.
      level "6. Bridge and breach" "stone" 280 [ (0, 126, 124, 24); (136, 126, 144, 24); (190, 96, 18, 30) ] hatch (region 250 116 16 10) noHaz 12 14 8 2500 (inv [ "Builder", 4; "Basher", 3; "Blocker", 2; "Bomber", 1 ])
      // 7 — bash through the first wall, then climb the tall one to the exit on top.
      level "7. Break and scale" "brick" 280 [ groundOf 280; (90, 96, 18, 30); (180, 40, 16, 86) ] hatch (region 180 30 16 10) noHaz 10 14 6 2200 (inv [ "Basher", 3; "Climber", 6; "Blocker", 2; "Bomber", 1 ])
      // 8 — mine down off the plateau, then bash through the wall to the exit.
      level "8. Mine and breach" "earth" 260 [ groundOf 260; (0, 60, 120, 90); (190, 96, 18, 30) ] (region 16 42 8 4) (region 240 116 16 10) noHaz 12 14 8 2600 (inv [ "Miner", 3; "Basher", 3; "Blocker", 2; "Bomber", 1 ])
      // 9 — everything unlocked: get off the plateau (mine/float), bash the wall,
      // bridge the lava. Three tools, generous supply.
      level "9. Anything goes" "hell" 600 [ groundOf 600; (0, 60, 150, 90); (320, 96, 18, 30) ] (region 16 42 8 4) (region 560 116 16 10) [ hazard "lava" 430 126 12 24 ] 14 12 8 3200 (inv [ "Miner", 4; "Basher", 4; "Builder", 6; "Floater", 8; "Digger", 3; "Climber", 4; "Blocker", 3; "Bomber", 2 ])
      // 10 — bridge the lava AND the water; a steel patch is walkable but can't be
      // dug through (try it).
      level "10. Inferno" "hell" 360 [ groundOf 360 ] hatch (region 332 116 16 10) [ hazard "steel" 60 126 40 24; hazard "lava" 120 126 12 24; hazard "water" 210 126 12 24 ] 10 16 4 2800 (inv [ "Builder", 12; "Floater", 6; "Digger", 3; "Blocker", 4; "Bomber", 2 ]) ]

// Design rule: a level offering Blockers must also offer Bombers (to free a
// trapped blocker), and its save target must leave margin for those sacrifices.
campaign
|> List.iter (fun lvl ->
    let has s = lvl.inventory |> List.exists (fun p -> p.skill = s)
    if has "Blocker" && not (has "Bomber") then
        failwithf "Level '%s' offers Blocker but no Bomber" lvl.name)

let opts = JsonSerializerOptions(WriteIndented = false)
let json = JsonSerializer.Serialize(campaign, opts)
let out = Path.Combine(__SOURCE_DIRECTORY__, "..", "src", "levels.json")
File.WriteAllText(out, json)
printfn "Wrote %s  (%d levels)" (Path.GetFullPath out) (List.length campaign)
