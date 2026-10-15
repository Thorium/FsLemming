// Level commands for scripts and agents: check, solve, render to PNG and
// compare levels — the same check as the editor's Check button
// (src/LevelCheck.fs), running the game's own rules.
//
//   dotnet fsi tools/levels.fsx --check [levels.json] [--level N] [--json]
//   dotnet fsi tools/levels.fsx --solve [levels.json] [--level N]
//   dotnet fsi tools/levels.fsx --render [levels.json] <out.png|folder> [--level N] [--route] [--scale N]
//   dotnet fsi tools/levels.fsx --compare <before.json> <after.json> <out.png|folder> [--level N] [--scale N]
//
// The levels file defaults to src/levels.json; the editor's downloaded
// levels.json works too. --level picks one level by number (1-based) or by part
// of its name; without it every level in the file is used.
// Exit codes: 0 done, 1 the check found a problem, 2 bad arguments or files.

#load "LevelFile.fsx"

open System
open System.IO
open System.Text.Json
open FsLemming.Types
open FsLemming.LevelCheck
open LevelFile

let usage =
    [ "Usage: dotnet fsi tools/levels.fsx <command> ..."
      "  --check [levels.json] [--level N] [--json]"
      "      hatch, exit, numbers, the no-skill walk and a solution; --json lists"
      "      every finding with its spot. Exit code 1 when a problem is found."
      "  --solve [levels.json] [--level N]"
      "      the plan with the fewest skills (up to 3) that saves a lemming"
      "  --render [levels.json] <out.png|folder> [--level N] [--route] [--scale N]"
      "      the level as a PNG (scale 1-8, default 3); --route draws the no-skill"
      "      walk (grey), the solution (yellow) with its skill spots, and problems"
      "  --compare <before.json> <after.json> <out.png|folder> [--level N] [--scale N]"
      "      before | after | changes (green = new terrain, red = removed);"
      "      levels pair up by name"
      ""
      "The levels file defaults to src/levels.json. --level is a number (1-based)"
      "or part of a level's name. Exit codes: 0 done, 1 a problem found, 2 bad"
      "arguments or files." ]
    |> String.concat "\n"

exception UsageError of string

let fail msg = raise (UsageError msg)

// ─── Arguments ─────────────────────────────────────────────────────────────

let args = fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray

let flagValue (name: string) =
    match List.tryFindIndex ((=) name) args with
    | Some i when i + 1 < args.Length -> Some args[i + 1]
    | Some _ -> fail $"%s{name} needs a value"
    | None -> None

let hasFlag (name: string) = List.contains name args

let scale () =
    match flagValue "--scale" with
    | None -> 3
    | Some v ->
        match Int32.TryParse v with
        | true, n when n >= 1 && n <= 8 -> n
        | _ -> fail $"--scale must be 1-8, got '%s{v}'"

/// Arguments that aren't flags or flag values (after the command itself).
let positional =
    let valued = [ "--level"; "--scale" ]
    let rec go acc rest =
        match rest with
        | [] -> List.rev acc
        | f :: _ :: tail when List.contains f valued -> go acc tail
        | (f: string) :: tail when f.StartsWith "--" -> go acc tail
        | a :: tail -> go (a :: acc) tail
    go [] (List.tail args)

/// The levels in a file; a file that isn't a levels.json is an IOException
/// (exit code 2, never the check's 1).
let readLevels (path: string) =
    if not (File.Exists path) then raise (FileNotFoundException $"not found: %s{path}")
    try
        loadLevels path
    with
    | :? IOException -> reraise ()
    | ex -> raise (IOException($"%s{path} is not a levels.json file: %s{ex.Message}", ex))

/// The levels --level picks (all when it's absent), numbered from 1.
let pick (levels: Level list) =
    let numbered = levels |> List.mapi (fun i l -> i + 1, l)
    match flagValue "--level" with
    | None -> numbered
    | Some v ->
        let chosen =
            match Int32.TryParse v with
            | true, n -> numbered |> List.filter (fun (i, _) -> i = n)
            | _ -> numbered |> List.filter (fun (_, l) -> l.Name.Contains(v, StringComparison.OrdinalIgnoreCase))
        if chosen.IsEmpty then fail $"no level matches --level %s{v}"
        chosen

/// A file name from a level name, without a leading "3." level number.
let safeName (l: Level) =
    let name = Text.RegularExpressions.Regex.Replace(l.Name, @"^\s*\d+\.\s*", "")
    Text.RegularExpressions.Regex.Replace(name, "[^A-Za-z0-9]+", "-").Trim '-'

/// Where one level's picture goes: the path itself for one level, or a file
/// named after the level in the output folder.
let outputFor (output: string) (count: int) (i: int, l: Level) =
    if count = 1 && output.EndsWith(".png", StringComparison.OrdinalIgnoreCase) then
        match Path.GetDirectoryName(Path.GetFullPath output) with
        | null -> ()
        | dir -> Directory.CreateDirectory dir |> ignore
        output
    else
        Directory.CreateDirectory output |> ignore
        Path.Combine(output, $"%02d{i}-%s{safeName l}.png")

// ─── PNG ───────────────────────────────────────────────────────────────────

let writePng (path: string) (w: int) (h: int) (rgb: int[]) =
    let raw = Array.zeroCreate<byte> (h * (w * 3 + 1))
    for y in 0 .. h - 1 do
        for x in 0 .. w - 1 do
            let c = rgb[y * w + x]
            let j = y * (w * 3 + 1) + 1 + x * 3
            raw[j] <- byte ((c >>> 16) &&& 0xFF)
            raw[j + 1] <- byte ((c >>> 8) &&& 0xFF)
            raw[j + 2] <- byte (c &&& 0xFF)
    let crcTable =
        Array.init 256 (fun n ->
            let mutable c = uint32 n
            for _ in 0..7 do c <- if c &&& 1u <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1
            c)
    let crc (bytes: byte[]) =
        let mutable c = 0xFFFFFFFFu
        for b in bytes do c <- crcTable[int ((c ^^^ uint32 b) &&& 0xFFu)] ^^^ (c >>> 8)
        c ^^^ 0xFFFFFFFFu
    let be (v: uint32) = [| byte (v >>> 24); byte (v >>> 16); byte (v >>> 8); byte v |]
    use fs = File.Create path
    let chunk (kind: string) (data: byte[]) =
        let kd = Array.append (Text.Encoding.ASCII.GetBytes kind) data
        fs.Write(be (uint32 data.Length))
        fs.Write kd
        fs.Write(be (crc kd))
    fs.Write [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]
    chunk "IHDR" (Array.concat [ be (uint32 w); be (uint32 h); [| 8uy; 2uy; 0uy; 0uy; 0uy |] ])
    let z =
        use ms = new MemoryStream()
        (use zs = new Compression.ZLibStream(ms, Compression.CompressionLevel.Optimal)
         zs.Write(raw, 0, raw.Length))
        ms.ToArray()
    chunk "IDAT" z
    chunk "IEND" [||]

// ─── Pictures ──────────────────────────────────────────────────────────────

let rgb r g b = (r <<< 16) ||| (g <<< 8) ||| b

/// Editor colours: empty, dirt, steel, lava, water.
let cellColour (l: Level) i =
    if l.Steel[i] then rgb 112 120 138
    elif l.Terrain[i] then rgb 120 80 44
    elif l.Lava[i] then rgb 216 51 15
    elif l.Water[i] then rgb 42 108 208
    else rgb 20 20 35

let skillColour =
    function
    | Digger -> rgb 255 160 40 | Builder -> rgb 80 220 255 | Basher -> rgb 255 90 90
    | Miner -> rgb 200 120 255 | Climber -> rgb 120 255 120 | Floater -> rgb 255 255 255
    | Blocker -> rgb 160 160 160 | Bomber -> rgb 255 0 0 | Walker -> rgb 200 200 200

/// A level as a scaled picture: terrain, hatch and exit outlines, and with
/// `route` the no-skill walk, the solution and the check's problem spots.
let levelPicture (l: Level) (k: int) (route: Report option) =
    let w, h = l.Width * k, l.Height * k
    let px = Array.init (w * h) (fun i -> cellColour l ((i / w / k) * l.Width + (i % w) / k))
    let put x y c = if x >= 0 && x < w && y >= 0 && y < h then px[y * w + x] <- c
    let box (r: Region) c =
        for x in r.X * k .. (r.X + r.W) * k - 1 do
            put x (r.Y * k) c
            put x ((r.Y + r.H) * k - 1) c
        for y in r.Y * k .. (r.Y + r.H) * k - 1 do
            put (r.X * k) y c
            put ((r.X + r.W) * k - 1) y c
    let dot x y size c =
        for dy in -size .. size do
            for dx in -size .. size do
                put (x * k + k / 2 + dx) (y * k + k / 2 + dy) c
    box l.Hatch (rgb 200 140 70)
    box l.Exit (rgb 40 220 90)
    match route with
    | None -> ()
    | Some r ->
        for x, y in r.NoSkills.Trace do dot x y 0 (rgb 150 150 150)
        match r.Solution with
        | Some s ->
            for x, y in s.Trace do dot x y 0 (rgb 255 230 60)
            for a in s.Assignments do dot a.X a.Y (max 1 (k / 2 + 1)) (skillColour a.Skill)
        | None -> ()
        // where the no-skill walk ends badly, and every problem with a spot
        if r.NoSkills.End <> Saved then dot r.NoSkills.EndX r.NoSkills.EndY (max 1 (k / 2)) (rgb 255 40 40)
        for f in r.Findings do
            if f.X >= 0 && f.Severity <> Note then
                for d in -2 * k .. 2 * k do
                    put (f.X * k + d) (f.Y * k + d) (rgb 255 0 255)
                    put (f.X * k + d) (f.Y * k - d) (rgb 255 0 255)
    w, h, px

/// Pictures side by side with a grey gap.
let sideBySide (pictures: (int * int * int[]) list) =
    let gap = 6
    let w = (pictures |> List.sumBy (fun (pw, _, _) -> pw)) + gap * (pictures.Length - 1)
    let h = pictures |> List.map (fun (_, ph, _) -> ph) |> List.max
    let px = Array.create (w * h) (rgb 60 60 60)
    let mutable ox = 0
    for pw, ph, p in pictures do
        for y in 0 .. ph - 1 do
            for x in 0 .. pw - 1 do
                px[y * w + ox + x] <- p[y * pw + x]
        ox <- ox + pw + gap
    w, h, px

// ─── Commands ──────────────────────────────────────────────────────────────

let severityName = function Problem -> "problem" | Warning -> "warning" | Note -> "note"

let endName =
    function
    | Saved -> "saved" | Died(Some Splat) -> "splat" | Died(Some Burn) -> "burn"
    | Died(Some Drown) -> "drown" | Died(Some Explode) -> "explode" | Died None -> "fell-off-map"
    | Stuck -> "stuck" | OutOfTime -> "out-of-time"

// JSON shapes of --check --json (named records keep the field order).
type SkillJson = { Skill: string; Tick: int; X: int; Y: int }
type RouteJson = { End: string; X: int; Y: int; Ticks: int; Skills: SkillJson list }
type FindingJson = { Severity: string; Code: string; Message: string; X: int; Y: int }
type LevelJson =
    { Number: int
      Level: string
      Playable: bool
      Findings: FindingJson list
      NoSkills: RouteJson
      Solution: RouteJson option
      SearchComplete: bool }

let routeJson (r: Route) =
    { End = endName r.End
      X = r.EndX
      Y = r.EndY
      Ticks = r.Ticks
      Skills = r.Assignments |> List.map (fun a -> { Skill = $"%A{a.Skill}"; Tick = a.Tick; X = a.X; Y = a.Y }) }

let checkCommand () =
    let file = match positional with [] -> campaignPath | [ f ] -> f | _ -> fail "--check takes one levels file"
    let results = readLevels file |> pick |> List.map (fun (i, l) -> i, l, check l)
    if hasFlag "--json" then
        let out =
            results
            |> List.map (fun (i, l, r) ->
                { Number = i
                  Level = l.Name
                  Playable = r.Findings |> List.forall (fun f -> f.Severity <> Problem)
                  Findings = r.Findings |> List.map (fun f -> { Severity = severityName f.Severity; Code = f.Code; Message = f.Message; X = f.X; Y = f.Y })
                  NoSkills = routeJson r.NoSkills
                  Solution = r.Solution |> Option.map routeJson
                  SearchComplete = r.SearchComplete })
        let options = JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
        Console.WriteLine(JsonSerializer.Serialize(out, options))
    else
        for i, l, r in results do
            Console.WriteLine($"[%d{i}] %s{l.Name}")
            for f in r.Findings do
                Console.WriteLine($"   %-8s{severityName f.Severity} %s{f.Message}")
    if results |> List.exists (fun (_, _, r) -> r.Findings |> List.exists (fun f -> f.Severity = Problem)) then 1 else 0

let solveCommand () =
    let file = match positional with [] -> campaignPath | [ f ] -> f | _ -> fail "--solve takes one levels file"
    for i, l in readLevels file |> pick do
        match solve l with
        | Some plan, _ ->
            let r = simulate l plan false
            let steps = if plan.IsEmpty then "no skills needed" else plan |> List.map (fun a -> $"%A{a.Skill} at tick %d{a.Tick} (%d{a.X}, %d{a.Y})") |> String.concat "; "
            Console.WriteLine($"%s{l.Name}: %s{steps}; saved after %d{r.Ticks} ticks")
        | None, complete ->
            Console.WriteLine($"""%s{l.Name}: no plan of up to %d{MaxSkills} skills%s{if complete then "" else " within the search limit"}""")
    0

let renderCommand () =
    let file, output =
        match positional with
        | [ o ] -> campaignPath, o
        | [ f; o ] -> f, o
        | _ -> fail "--render takes [levels.json] <out.png|folder>"
    let k = scale ()
    let chosen = readLevels file |> pick
    for i, l in chosen do
        let report = if hasFlag "--route" then Some(check l) else None
        let w, h, px = levelPicture l k report
        let out = outputFor output chosen.Length (i, l)
        writePng out w h px
        Console.WriteLine($"%s{l.Name} -> %s{out}")
    0

let compareCommand () =
    let before, after, output =
        match positional with
        | [ b; a; o ] -> b, a, o
        | _ -> fail "--compare takes <before.json> <after.json> <out.png|folder>"
    let k = scale ()
    let afterLevels = readLevels after
    let pairs =
        readLevels before
        |> pick
        |> List.choose (fun (i, b) -> afterLevels |> List.tryFind (fun a -> a.Name = b.Name) |> Option.map (fun a -> i, b, a))
    if pairs.IsEmpty then fail "no level has the same name in both files"
    for i, b, a in pairs do
        let changes =
            if a.Width <> b.Width || a.Height <> b.Height then levelPicture a k None
            else
                let w, h, px = levelPicture a k None
                for y in 0 .. h - 1 do
                    for x in 0 .. w - 1 do
                        let c = (y / k) * a.Width + x / k
                        px[y * w + x] <-
                            if a.Terrain[c] && not b.Terrain[c] then rgb 0 220 0
                            elif b.Terrain[c] && not a.Terrain[c] then rgb 255 40 40
                            else let p = px[y * w + x] in rgb (((p >>> 16) &&& 0xFF) / 3) (((p >>> 8) &&& 0xFF) / 3) ((p &&& 0xFF) / 3)
                w, h, px
        let w, h, px = sideBySide [ levelPicture b k None; levelPicture a k None; changes ]
        let out = outputFor output pairs.Length (i, b)
        writePng out w h px
        Console.WriteLine($"%s{b.Name} -> %s{out}")
    0

let exitCode =
    try
        match args with
        | "--check" :: _ -> checkCommand ()
        | "--solve" :: _ -> solveCommand ()
        | "--render" :: _ -> renderCommand ()
        | "--compare" :: _ -> compareCommand ()
        | _ -> fail "pick a command"
    with
    | UsageError msg ->
        eprintfn "Error: %s\n\n%s" msg usage
        2
    | :? IOException as ex ->
        eprintfn "Error: %s" ex.Message
        2

exit exitCode
