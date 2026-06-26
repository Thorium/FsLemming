// Generate the FsLemming sprite sheet — ORIGINAL pixel art (not ripped from the
// original game), so it is safe to publish. 16x16 frames, one COLUMN per frame
// and one ROW per skin-tone variant.
//
// Frame order (must match src/Sprites.fs):
//   walk0..3  fall0..1  dig0..1  bash0..1  mine0..1
//   block0..1 build0..1 climb0..1 float0..1
//
// Cute chibi look: big round head with large eyes, small blue body, bright
// orange feet (visible against the dark sky). A 4-frame walk and 2-frame idle
// poses keep the crowd lively.
//
// Run with:  dotnet fsi tools/gen_sprites.fsx [output.png]
// Pure BCL — no NuGet packages (ZLibStream for zlib, hand-rolled CRC-32).
// Legend: . transparent  g hair  s skin  b body  w white(eyes/umbrella)
//         k pupil  o orange feet  r brick  p steel pickaxe

open System.IO
open System.IO.Compression
open System.Text

// Fixed colours.
let HAIR = [| 34uy; 182uy; 60uy; 255uy |]
let BODY = [| 43uy; 79uy; 224uy; 255uy |]
let DARK = [| 28uy; 28uy; 48uy; 255uy |]
let WHITE = [| 240uy; 242uy; 255uy; 255uy |]
let FEET = [| 255uy; 150uy; 45uy; 255uy |]
let BRICK = [| 165uy; 110uy; 70uy; 255uy |] // brick a builder carries / lays
let PICK = [| 228uy; 232uy; 242uy; 255uy |] // miner's steel pickaxe (bright, visible)
let TRANSPARENT = [| 0uy; 0uy; 0uy; 0uy |]

let SKINS =
    [ [| 255uy; 224uy; 189uy; 255uy |]
      [| 241uy; 194uy; 125uy; 255uy |]
      [| 198uy; 134uy; 66uy; 255uy |]
      [| 120uy; 72uy; 42uy; 255uy |] ]

// Shared head (rows 0-7) for the upright poses.
let HEAD =
    [ "................"
      "....gggggg......"
      "...gggggggg....."
      "...gssssssg....."
      "...gwwsswwg....."
      "...gwksskwg....."
      "...gssssssg....."
      "....sssss......." ]

// Lower bodies (rows 8-15); composed with HEAD.
let walk0 =
    [ ".....bbbbb......"; "....bbbbbbb....."; ".....bbbbb......"; ".....bbbbb......"
      ".....b.b........"; "....oo.oo......."; "................"; "................" ]

let walk1 =
    [ ".....bbbbb......"; "....bbbbbbb....."; ".....bbbbb......"; ".....bbbbb......"
      ".....bbb........"; ".....o.o........"; "................"; "................" ]

let walk2 =
    [ ".....bbbbb......"; "....bbbbbbb....."; ".....bbbbb......"; ".....bbbbb......"
      "....bb.bb......."; "...oo...oo......"; "................"; "................" ]

let walk3 =
    [ ".....bbbbb......"; "....bbbbbbb....."; ".....bbbbb......"; ".....bbbbb......"
      ".....bbb........"; "......o.o......."; "................"; "................" ]

let fall0 =
    [ "..b..bbb..b....."; "...bbbbbbb......"; "....bbbbb......."; "....b...b......."
      "...o.....o......"; "................"; "................"; "................" ]

let fall1 =
    [ ".b...bbb...b...."; "...bbbbbbb......"; "....bbbbb......."; "....b...b......."
      "..o.......o....."; "................"; "................"; "................" ]

// Digger: crouched in the hole, scooping dirt — hands pump DOWN (DIG0) then UP
// (DIG1) while the head stays put, so the arms clearly alternate.
let DIG0 = // hands down, scooping into the dirt
    [ "................"; "................"; "................"; "................"
      "....gggggg......"; "...gwwsswwg....."; "...gwksskwg....."; "...gssssssg....."
      "...bsssssb......"; "...b.bbb.b......"; "...o.bbb.o......"; "....bb.bb......."
      "....oo.oo......."; "................"; "................"; "................" ]

let DIG1 = // hands up, tossing the dirt out
    [ "................"; "....o....o......"; "....b....b......"; "....b....b......"
      "....gggggg......"; "...gwwsswwg....."; "...gwksskwg....."; "...gssssssg....."
      "....sssss......."; "....bbbbb......."; "....bbbbb......."; "....bb.bb......."
      "....oo.oo......."; "................"; "................"; "................" ]

let bash0 =
    [ ".....bbbbbss...."; "....bbbbbbb....."; ".....bbbbb......"; ".....bbbbb......"
      ".....b.b........"; "....oo.oo......."; "................"; "................" ]

let bash1 =
    [ ".....bbbbb......"; "....bbbbbb......"; ".....bbbbb......"; ".....bbbbb......"
      ".....b.b........"; "....oo.oo......."; "................"; "................" ]

// Blocker: arms straight out at 90° (held still); the HEAD turns side-to-side
// ("no") between frames, arms/body/feet stay put.
let BLOCK0 = // head turned left
    [ "................"; "...gggggg......."; "..gggggggg......"; "..gssssssg......"
      "..gwwsswwg......"; "..gwksskwg......"; "..gssssssg......"; "...sssss........"
      "..obbbbbbbbbbo.."; ".....bbbbb......"; ".....bbbbb......"; ".....bbbbb......"
      ".....bb.bb......"; "....oo.oo......."; "................"; "................" ]

let BLOCK1 = // head turned right
    [ "................"; ".....gggggg....."; "....gggggggg...."; "....gssssssg...."
      "....gwwsswwg...."; "....gwksskwg...."; "....gssssssg...."; ".....sssss......"
      "..obbbbbbbbbbo.."; ".....bbbbb......"; ".....bbbbb......"; ".....bbbbb......"
      ".....bb.bb......"; "....oo.oo......."; "................"; "................" ]

// Builder: leans toward the work (head shifted toward build direction), holding a
// brick (rr) forward — moves it down to lay it.
let BUILD0 =
    [ "................"; "................"; ".....gggggg....."; "....gggggggg...."
      "....gggggggg...."; "....gwwsswwg...."; "....gwksskwg...."; ".....sssss......"
      ".....bbbbbsrr..."; ".....bbbbb......"; ".....bbbbb......"; ".....bbbbb......"
      ".....bb.bb......"; "....oo.oo......."; "................"; "................" ]

let BUILD1 =
    [ "................"; "................"; ".....gggggg....."; "....gggggggg...."
      "....gggggggg...."; "....gwwsswwg...."; "....gwksskwg...."; ".....sssss......"
      ".....bbbbb......"; ".....bbbbbsrr..."; ".....bbbb......."; ".....bbbbb......"
      ".....bb.bb......"; "....oo.oo......."; "................"; "................" ]

let compose head low = head @ low

// Custom full frames (their tops differ from the standard head).
// Climber: a side profile pressed against the wall (to the right; flipped for the
// left), one eye facing the wall, gripping hand reaching up the wall to push up.
// Lower legs bend toward the wall so the feet press flat against it (the wall is
// to the right; the renderer flips the sprite for a left-hand wall).
let CLIMB0 = // grips low
    [ "................"; "................"; ".....gggg......."; "....gggggs......"
      "....gggkss......"; "....ggggss......"; ".....bbb........"; ".....bbb........"
      ".....bbbo......."; ".....bbb........"; ".....bbb........"; "......bb........"
      ".......bbo......"; ".......boo......"; "................"; "................" ]

let CLIMB1 = // grips high (push up); feet shuffle 1px up for a tiny climbing bob
    [ "................"; "................"; ".....gggg......."; "....gggggs......"
      "....gggkss......"; "....ggggss......"; ".....bbbo......."; ".....bbb........"
      ".....bbb........"; ".....bbb........"; "......bb........"; ".......bbo......"
      ".......boo......"; "................"; "................"; "................" ]

let FLOAT0 =
    [ "................"; "...wwwwwww......"; "..wwwwwwwww....."; ".......k........"
      "....gggggg......"; "...gwwsswwg....."; "...gwksskwg....."; "....sssss......."
      ".....bbbbb......"; "....bbbbbbb....."; ".....bbbbb......"; ".....b.b........"
      "....oo.oo......."; "................"; "................"; "................" ]

let FLOAT1 =
    [ "................"; "..wwwwwwwww....."; "...wwwwwww......"; ".......k........"
      "....gggggg......"; "...gwwsswwg....."; "...gwksskwg....."; "....sssss......."
      ".....bbbbb......"; "....bbbbbbb....."; ".....bbbbb......"; ".....bbb........"
      ".....o.o........"; "................"; "................"; "................" ]

// Miner: crouched, swinging a bright steel pickaxe (p) down-and-forward into the
// slope. The pick sweeps from high (MINE1) to struck-at-ground (MINE0).
let MINE0 = // pickaxe struck down-right, curved head (-)  at the ground
    [ "................"; "................"; "................"; "................"
      "...gggggggg....."; "...gwwsswwg....."; "...gwksskwg....."; "...gssssssgs...."
      "....bbbbb..p...."; "....bbbbbb.p...."; ".....bbb...pp..."; ".....o.o.....p.."
      "...........pp..."; "................"; "................"; "................" ]

let MINE1 = // pickaxe raised up-right, curved head (-)  held high
    [ "............pp.."; "..............p."; "............pp.."; "............p..."
      "...gggggggg.p..."; "...gwwsswwgsp..."; "...gwksskwg....."; "...gssssssg....."
      "....bbbbb......."; "....bbbbbb......"; ".....bbb........"; ".....o.o........"
      "................"; "................"; "................"; "................" ]

let FRAMES =
    [ compose HEAD walk0; compose HEAD walk1; compose HEAD walk2; compose HEAD walk3
      compose HEAD fall0; compose HEAD fall1
      DIG0; DIG1
      compose HEAD bash0; compose HEAD bash1
      MINE0; MINE1
      BLOCK0; BLOCK1
      BUILD0; BUILD1
      CLIMB0; CLIMB1
      FLOAT0; FLOAT1 ]

let SIZE = 16
let LEGEND = set [ '.'; 'k'; 'g'; 's'; 'b'; 'w'; 'o'; 'r'; 'p' ]

// ---- validate ---------------------------------------------------------------
FRAMES
|> List.iteri (fun i frame ->
    if List.length frame <> SIZE then
        failwithf "frame %d: %d rows (need %d)" i (List.length frame) SIZE
    frame
    |> List.iteri (fun r line ->
        if String.length line <> SIZE then
            failwithf "frame %d row %d: len %d (need %d)" i r (String.length line) SIZE
        for ch in line do
            if not (Set.contains ch LEGEND) then
                failwithf "frame %d row %d: bad char %c" i r ch))

let colourFor (ch: char) (skin: byte[]) =
    match ch with
    | '.' -> TRANSPARENT
    | 'k' -> DARK
    | 'g' -> HAIR
    | 'b' -> BODY
    | 'w' -> WHITE
    | 'o' -> FEET
    | 'r' -> BRICK
    | 'p' -> PICK
    | 's' -> skin
    | _ -> failwithf "bad char %c" ch

// ---- build the RGBA buffer: a row of frames per skin tone -------------------
let W = SIZE * List.length FRAMES
let H = SIZE * List.length SKINS
let buf = Array.zeroCreate<byte> (W * H * 4)

SKINS
|> List.iteri (fun row skin ->
    FRAMES
    |> List.iteri (fun col frame ->
        let frame = List.toArray frame
        for y in 0 .. SIZE - 1 do
            let line = frame.[y]
            for x in 0 .. SIZE - 1 do
                let rgba = colourFor line.[x] skin
                let px = ((row * SIZE + y) * W + (col * SIZE + x)) * 4
                Array.blit rgba 0 buf px 4))

// ---- encode PNG (RGBA, 8-bit), no external deps -----------------------------
let crcTable =
    Array.init 256 (fun n ->
        let mutable c = uint32 n
        for _ in 0..7 do
            c <- if c &&& 1u <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1
        c)

let crc32 (data: byte[]) =
    let mutable c = 0xFFFFFFFFu
    for b in data do
        c <- crcTable.[int ((c ^^^ uint32 b) &&& 0xFFu)] ^^^ (c >>> 8)
    c ^^^ 0xFFFFFFFFu

let be32 (n: int) =
    [| byte (n >>> 24); byte (n >>> 16); byte (n >>> 8); byte n |]

let beU32 (n: uint32) =
    [| byte (n >>> 24); byte (n >>> 16); byte (n >>> 8); byte n |]

let chunk (tag: string) (data: byte[]) =
    let tagBytes = Encoding.ASCII.GetBytes tag
    let body = Array.append tagBytes data
    Array.concat [ be32 data.Length; body; beU32 (crc32 body) ]

let zlibCompress (data: byte[]) =
    use ms = new MemoryStream()
    (use z = new ZLibStream(ms, CompressionLevel.SmallestSize, true)
     z.Write(data, 0, data.Length))
    ms.ToArray()

let encodePng (w: int) (h: int) (pixels: byte[]) =
    let stride = w * 4
    let raw =
        let out = Array.zeroCreate<byte> (h * (stride + 1))
        for y in 0 .. h - 1 do
            out.[y * (stride + 1)] <- 0uy // filter byte: none
            Array.blit pixels (y * stride) out (y * (stride + 1) + 1) stride
        out
    let ihdr = Array.concat [ be32 w; be32 h; [| 8uy; 6uy; 0uy; 0uy; 0uy |] ]
    Array.concat
        [ [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]
          chunk "IHDR" ihdr
          chunk "IDAT" (zlibCompress raw)
          chunk "IEND" [||] ]

let outPath =
    let args = fsi.CommandLineArgs
    if args.Length > 1 then args.[1]
    else Path.Combine(__SOURCE_DIRECTORY__, "..", "public", "lemmings.png")

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath outPath)) |> ignore
File.WriteAllBytes(outPath, encodePng W H buf)
printfn "Wrote %s  (%dx%d, %d frames x %d skin tones)" outPath W H (List.length FRAMES) (List.length SKINS)

// ---- favicon: a single walking lemming, scaled up (nearest-neighbour) --------
let favScale = 8
let favSize = SIZE * favScale
let favFrame = List.toArray FRAMES.[0] // walk0
let favBuf = Array.zeroCreate<byte> (favSize * favSize * 4)
for y in 0 .. favSize - 1 do
    let line = favFrame.[y / favScale]
    for x in 0 .. favSize - 1 do
        let rgba = colourFor line.[x / favScale] SKINS.[0]
        Array.blit rgba 0 favBuf ((y * favSize + x) * 4) 4

let favPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath outPath), "favicon.png")
File.WriteAllBytes(favPath, encodePng favSize favSize favBuf)
printfn "Wrote %s  (%dx%d)" favPath favSize favSize
