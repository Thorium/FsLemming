// Shared by the .fsx tools: load the game's own modules and read levels.json
// (or a levels file the editor downloaded) into the game's Level values. The
// tools then run the very same rules as the game — Lemming.step and
// World.applyMutation — through src/LevelCheck.fs, so they can't drift apart.
//
//   #load "LevelFile.fsx"

#load "../src/Types.fs" "../src/World.fs" "../src/Lemming.fs" "../src/Inventory.fs" "../src/Game.fs" "../src/LevelCheck.fs"

open System.IO
open System.Text.Json
open FsLemming.Types

/// The campaign file the game ships.
let campaignPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "src", "levels.json"))

/// Expand RLE runs (alternating empty/solid, starting empty) into a flat grid.
let rleDecode (runs: int seq) (size: int) =
    let cells: bool[] = Array.zeroCreate size
    let mutable i = 0
    let mutable v = false
    for run in runs do
        for _ in 1..run do
            if i < size then cells.[i] <- v
            i <- i + 1
        v <- not v
    cells

let private tryProp (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind <> JsonValueKind.Null -> ValueSome v
    | _ -> ValueNone

let private int' (e: JsonElement) (name: string) = e.GetProperty(name).GetInt32()

let private region (e: JsonElement) : Region =
    { X = int' e "x"; Y = int' e "y"; W = int' e "w"; H = int' e "h" }

let private mask (e: JsonElement) (name: string) size =
    match tryProp e name with
    | ValueSome runs -> rleDecode (runs.EnumerateArray() |> Seq.map (fun r -> r.GetInt32())) size
    | ValueNone -> Array.zeroCreate size // older files without steel / lava / water

let private skillOf =
    function
    | "Walker" -> Some Walker | "Digger" -> Some Digger | "Blocker" -> Some Blocker
    | "Builder" -> Some Builder | "Basher" -> Some Basher | "Miner" -> Some Miner
    | "Climber" -> Some Climber | "Floater" -> Some Floater | "Bomber" -> Some Bomber
    | _ -> None

let private themeOf =
    function
    | "stone" -> Stone | "brick" -> Brick | "hell" -> Hell
    | _ -> Earth

/// One level from its JSON object (the shape src/LevelJson.fs reads).
let decodeLevel (e: JsonElement) : Level =
    let w, h = int' e "width", int' e "height"
    let size = w * h
    { Name = e.GetProperty("name").GetString()
      Theme = (match tryProp e "theme" with ValueSome t -> themeOf (t.GetString()) | ValueNone -> Earth)
      Width = w
      Height = h
      Terrain = mask e "terrain" size
      Steel = mask e "steel" size
      Lava = mask e "lava" size
      Water = mask e "water" size
      Hatch = region (e.GetProperty "hatch")
      Exit = region (e.GetProperty "exit")
      Decor =
        match tryProp e "decor" with
        | ValueSome d -> d.EnumerateArray() |> Seq.map (fun p -> { X = int' p "x"; Y = int' p "y"; V = int' p "v" }: Decor) |> List.ofSeq
        | ValueNone -> []
      Pillars =
        match tryProp e "pillars" with
        | ValueSome p -> p.EnumerateArray() |> Seq.map region |> List.ofSeq
        | ValueNone -> []
      SpawnCount = int' e "spawnCount"
      SpawnEveryTicks = int' e "spawnEveryTicks"
      SaveTarget = int' e "saveTarget"
      TimeLimitTicks = int' e "timeLimitTicks"
      Inventory =
        e.GetProperty("inventory").EnumerateArray()
        |> Seq.choose (fun p -> skillOf (p.GetProperty("skill").GetString()) |> Option.map (fun s -> s, int' p "count"))
        |> Map.ofSeq }

/// Every level in a levels file (an array of levels, or a single level object).
let loadLevels (path: string) : Level list =
    use doc = JsonDocument.Parse(File.ReadAllText path)
    match doc.RootElement.ValueKind with
    | JsonValueKind.Array -> doc.RootElement.EnumerateArray() |> Seq.map decodeLevel |> List.ofSeq
    | _ -> [ decodeLevel doc.RootElement ]
