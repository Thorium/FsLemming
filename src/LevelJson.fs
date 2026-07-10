module FsLemming.LevelJson

open Fable.Core
open Fable.Core.JsInterop
open FsLemming.Types

let private skillName =
    function
    | Walker -> "Walker"
    | Digger -> "Digger"
    | Blocker -> "Blocker"
    | Builder -> "Builder"
    | Basher -> "Basher"
    | Miner -> "Miner"
    | Climber -> "Climber"
    | Floater -> "Floater"
    | Bomber -> "Bomber"

// Decodes the JSON produced by tools/gen_levels.fsx (and, later, the level
// editor) into `Level` values. Vite parses the .json at import time, so we
// receive a plain JS object — no JSON.parse needed; we just read fields.

let private parseSkill (s: string) : Skill option =
    match s with
    | "Walker" -> Some Walker
    | "Digger" -> Some Digger
    | "Blocker" -> Some Blocker
    | "Builder" -> Some Builder
    | "Basher" -> Some Basher
    | "Miner" -> Some Miner
    | "Climber" -> Some Climber
    | "Floater" -> Some Floater
    | "Bomber" -> Some Bomber
    | _ -> None

let private parseTheme =
    function
    | "stone" -> Stone
    | "brick" -> Brick
    | "hell" -> Hell
    | _ -> Earth

let private themeName =
    function
    | Earth -> "earth"
    | Stone -> "stone"
    | Brick -> "brick"
    | Hell -> "hell"

/// Expand RLE runs (alternating empty/solid, starting empty) into a flat grid.
let private rleDecode (runs: int[]) (size: int) : bool[] =
    let cells = Array.zeroCreate size
    let mutable i = 0
    let mutable value = false
    for run in runs do
        for _ in 1..run do
            if i < size then cells.[i] <- value
            i <- i + 1
        value <- not value
    cells

// The known JSON shapes, typed as erased anonymous records: `unbox` still marks
// the trust boundary (no runtime validation), but every field access after it
// is compiler-checked instead of stringly-dynamic.
let private region (o: {| x: int; y: int; w: int; h: int |}) : Region = { X = o.x; Y = o.y; W = o.w; H = o.h }

/// Decode an optional RLE field into a flat mask; absent → all-false (handles
/// older JSON written before steel/lava/water existed).
let private maskOf (v: obj) (size: int) : bool[] =
    if isNull (box v) then Array.zeroCreate size else rleDecode (unbox v) size

/// Decode the optional decor array; absent → no scenery (older JSON).
let private decorOf (v: obj) : Decor list =
    if isNull (box v) then
        []
    else
        unbox<{| x: int; y: int; v: int |}[]> v
        |> Array.map (fun d -> { X = d.x; Y = d.y; V = d.v }: Decor)
        |> Array.toList

/// Decode the optional pillars array; absent → none (older JSON).
let private pillarsOf (v: obj) : Region list =
    if isNull (box v) then
        []
    else
        unbox<{| x: int; y: int; w: int; h: int |}[]> v |> Array.map region |> Array.toList

let private decodeLevel (o: obj) : Level =
    let width: int = o?width
    let height: int = o?height
    let runs: int[] = o?terrain
    let invArr: obj[] = o?inventory
    let size = width * height

    { Name = o?name
      Theme = parseTheme (o?theme)
      Width = width
      Height = height
      Terrain = rleDecode runs size
      Steel = maskOf (o?steel) size
      Lava = maskOf (o?lava) size
      Water = maskOf (o?water) size
      Hatch = region o?hatch
      Exit = region o?exit
      Decor = decorOf o?decor
      Pillars = pillarsOf o?pillars
      SpawnCount = o?spawnCount
      SpawnEveryTicks = o?spawnEveryTicks
      SaveTarget = o?saveTarget
      TimeLimitTicks = o?timeLimitTicks
      Inventory =
        invArr
        |> Array.choose (fun p ->
            let count: int = p?count
            match parseSkill (p?skill) with
            | Some s -> Some(s, count)
            | None -> None)
        |> Map.ofArray }

/// Decode the campaign array (the default export of levels.json).
let decodeCampaign (data: obj) : Level list =
    let arr: obj[] = unbox data
    arr |> Array.map decodeLevel |> Array.toList

/// Decode a campaign from a JSON string (uploaded file or localStorage).
let decodeCampaignJson (json: string) : Level list = JS.JSON.parse json |> decodeCampaign

// ---- Encoding (mirror of the above; used by the level editor) ----------------

/// Collapse a grid into RLE runs (alternating empty/solid, starting empty).
let private rleEncode (cells: bool[]) : int[] =
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

let private regionObj (r: Region) =
    createObj [ "x", box r.X; "y", box r.Y; "w", box r.W; "h", box r.H ]

let private levelObj (lvl: Level) =
    createObj
        [ "name", box lvl.Name
          "theme", box (themeName lvl.Theme)
          "width", box lvl.Width
          "height", box lvl.Height
          "terrain", box (rleEncode lvl.Terrain)
          "steel", box (rleEncode lvl.Steel)
          "lava", box (rleEncode lvl.Lava)
          "water", box (rleEncode lvl.Water)
          "hatch", regionObj lvl.Hatch
          "exit", regionObj lvl.Exit
          "decor",
          box (
              lvl.Decor
              |> List.map (fun d -> createObj [ "x", box d.X; "y", box d.Y; "v", box d.V ])
              |> List.toArray
          )
          "pillars", box (lvl.Pillars |> List.map regionObj |> List.toArray)
          "spawnCount", box lvl.SpawnCount
          "spawnEveryTicks", box lvl.SpawnEveryTicks
          "saveTarget", box lvl.SaveTarget
          "timeLimitTicks", box lvl.TimeLimitTicks
          "inventory",
          box (
              lvl.Inventory
              |> Map.toArray
              |> Array.map (fun (s, n) -> createObj [ "skill", box (skillName s); "count", box n ])
          ) ]

/// Serialise levels to the same JSON shape gen_levels.fsx produces.
let encodeCampaign (levels: Level list) : string =
    levels |> List.map levelObj |> List.toArray |> box |> JS.JSON.stringify
