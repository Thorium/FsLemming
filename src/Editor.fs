module FsLemming.Editor

// A standalone level editor page (editor.html). Import an image → threshold to
// terrain, click to place the hatch/exit, set parameters, and export the very
// same levels.json format the game loads. Reuses the domain types and the
// LevelJson encoder — the engine is untouched.

open Browser
open Browser.Types
open Fable.Core
open Fable.Core.JsInterop
open FsLemming.Types

let private canvas = document.getElementById "edit" :?> HTMLCanvasElement
let private ctx = canvas.getContext_2d ()
let private byId (id: string) = document.getElementById id

let private H = 150
let mutable private W = 240
// Four parallel material masks. A cell holds at most one material.
let mutable private terrain: bool[] = Array.zeroCreate (W * H) // solid (dirt or steel)
let mutable private steel: bool[] = Array.zeroCreate (W * H) // indestructible subset of solid
let mutable private lava: bool[] = Array.zeroCreate (W * H)
let mutable private water: bool[] = Array.zeroCreate (W * H)
let mutable private hatch = { X = 16; Y = 108; W = 8; H = 4 }
let mutable private exit = { X = 212; Y = 116; W = 16; H = 10 }
// Brush: dirt | steel | lava | water | erase | hatch | exit | decor | pillar
let mutable private placeMode = "dirt"

// Cosmetic layers: scenery props (stamped on the surface) and pillar regions
// (dragged out as rectangles; the game dresses those blocks as fluted columns).
let mutable private decor: Decor list = []
let mutable private pillars: Region list = []
let mutable private decorV = 0 // next prop variant; cycles so stamps vary
let mutable private pillarStart: (int * int) option = None
let mutable private pillarPreview: Region option = None

let private groundTerrain w =
    let a = Array.zeroCreate (w * H)
    for y in 126 .. H - 1 do
        for x in 0 .. w - 1 do
            a.[y * w + x] <- true
    a

// ---- Preview: the whole level scaled into the fixed editor canvas -----------
let private redraw () =
    let cw = int canvas.width
    let ch = int canvas.height
    let img = ctx.createImageData (float cw, float ch)
    let data: byte[] = unbox img.data

    for cy in 0 .. ch - 1 do
        let wy = cy * H / ch
        for cx in 0 .. cw - 1 do
            let wx = cx * W / cw
            let p = (cy * cw + cx) * 4
            let i = wy * W + wx

            let r, g, b =
                if steel.[i] then 112uy, 120uy, 138uy // grey steel
                elif terrain.[i] then 120uy, 80uy, 44uy // dirt
                elif lava.[i] then 216uy, 51uy, 15uy
                elif water.[i] then 42uy, 108uy, 208uy
                else 20uy, 20uy, 35uy // empty

            data.[p] <- r
            data.[p + 1] <- g
            data.[p + 2] <- b
            data.[p + 3] <- 255uy

    ctx.putImageData (img, 0., 0.)

    let scaleX = float cw / float W
    let scaleY = float ch / float H

    // Hatch/exit as clearly-visible labelled markers (min size, outlined).
    let marker (r: Region) (fill: string) (label: string) =
        let x = float r.X * scaleX
        let y = float r.Y * scaleY
        let w = max 14.0 (float r.W * scaleX)
        let h = max 14.0 (float r.H * scaleY)
        ctx?fillStyle <- fill
        ctx.fillRect (x, y, w, h)
        ctx?strokeStyle <- "#ffffff"
        ctx.lineWidth <- 1.0
        ctx.strokeRect (x, y, w, h)
        ctx?fillStyle <- "#ffffff"
        ctx?textAlign <- "center"
        ctx?textBaseline <- "middle"
        ctx.font <- "9px monospace"
        ctx.fillText (label, x + w / 2.0, y + h / 2.0)

    marker hatch "#7a4a1e" "H"
    marker exit "#0c8a36" "E"

    // Cosmetic overlays: pillar regions (translucent, outlined) and decor stamps.
    let pillarRect (p: Region) =
        let x, y = float p.X * scaleX, float p.Y * scaleY
        let w, h = float p.W * scaleX, float p.H * scaleY
        ctx?fillStyle <- "rgba(216, 196, 137, 0.3)"
        ctx.fillRect (x, y, w, h)
        ctx?strokeStyle <- "#d8c489"
        ctx.lineWidth <- 1.0
        ctx.strokeRect (x, y, w, h)

    for p in pillars do
        pillarRect p

    match pillarPreview with
    | Some p -> pillarRect p
    | None -> ()

    for d in decor do
        let x, y = float d.X * scaleX, float d.Y * scaleY
        ctx?fillStyle <- "#3f9e42"
        ctx.fillRect (x - 2.0, y - 8.0, 5.0, 8.0)
        ctx?strokeStyle <- "#ffffff"
        ctx.strokeRect (x - 2.0, y - 8.0, 5.0, 8.0)

// ---- Editing: draw/erase terrain, place hatch/exit (click or drag) ----------
let mutable private painting = false

// Paint one material into the 7x7 brush footprint; a cell holds at most one
// material, so each is set exclusively (erase clears all).
let private brush (wx: int) (wy: int) (mat: string) =
    for dy in -3..3 do
        for dx in -3..3 do
            let x, y = wx + dx, wy + dy
            if x >= 0 && x < W && y >= 0 && y < H then
                let i = y * W + x
                terrain.[i] <- (mat = "dirt" || mat = "steel")
                steel.[i] <- (mat = "steel")
                lava.[i] <- (mat = "lava")
                water.[i] <- (mat = "water")

let private toWorld (clientX: float) (clientY: float) =
    let r = canvas.getBoundingClientRect ()
    int ((clientX - r.left) / r.width * float W), int ((clientY - r.top) / r.height * float H)

/// The ground row a stamped prop stands on: first solid cell at or below the
/// click, so props snap to the surface just like the generator places them.
let private surfaceBelow wx wy =
    if wx < 0 || wx >= W then None
    else [ max 0 wy .. H - 1 ] |> List.tryFind (fun y -> terrain.[y * W + wx])

let private normRect x0 y0 x1 y1 : Region =
    { X = min x0 x1; Y = min y0 y1; W = abs (x1 - x0) + 1; H = abs (y1 - y0) + 1 }

let private editAt (clientX: float) (clientY: float) =
    let wx, wy = toWorld clientX clientY

    match placeMode with
    | "dirt" | "steel" | "lava" | "water" | "erase" -> brush wx wy placeMode
    | "hatch" -> hatch <- { hatch with X = wx - hatch.W / 2; Y = wy - hatch.H / 2 }
    | "exit" -> exit <- { exit with X = wx - exit.W / 2; Y = wy - exit.H / 2 }
    | _ -> ()

    redraw ()

canvas.addEventListener ("mousedown", fun e ->
    let me = e :?> MouseEvent
    let wx, wy = toWorld me.clientX me.clientY

    match placeMode with
    | "decor" ->
        // Toggle: a click near an existing prop removes it, elsewhere stamps a
        // new one (variants cycle so repeated stamps vary).
        match decor |> List.tryFind (fun d -> abs (d.X - wx) <= 3 && abs (d.Y - wy) <= 8) with
        | Some d -> decor <- decor |> List.filter ((<>) d)
        | None ->
            match surfaceBelow wx wy with
            | Some y ->
                decor <- { X = wx; Y = y; V = decorV } :: decor
                decorV <- (decorV + 1) % 4
            | None -> ()

        redraw ()
    | "pillar" ->
        // A click inside an existing pillar removes it; otherwise start
        // dragging a new region (committed on mouseup).
        match pillars |> List.tryFind (fun p -> wx >= p.X && wx < p.X + p.W && wy >= p.Y && wy < p.Y + p.H) with
        | Some p ->
            pillars <- pillars |> List.filter ((<>) p)
            redraw ()
        | None -> pillarStart <- Some(wx, wy)
    | _ ->
        painting <- true
        editAt me.clientX me.clientY)

canvas.addEventListener ("mousemove", fun e ->
    let me = e :?> MouseEvent

    match pillarStart with
    | Some(sx, sy) ->
        let wx, wy = toWorld me.clientX me.clientY
        pillarPreview <- Some(normRect sx sy wx wy)
        redraw ()
    | None ->
        if painting then
            editAt me.clientX me.clientY)

window.addEventListener ("mouseup", fun _ ->
    painting <- false

    match pillarPreview with
    | Some p ->
        if p.W >= 4 && p.H >= 6 then
            pillars <- p :: pillars

        pillarStart <- None
        pillarPreview <- None
        redraw () // also clears a too-small preview rectangle
    | None -> pillarStart <- None)

// Brush selection. Each button highlights itself (CSS .selected) and sets the mode.
let private brushButtons =
    [ "tool-dirt", "dirt"; "tool-steel", "steel"; "tool-lava", "lava"; "tool-water", "water"
      "tool-erase", "erase"; "tool-decor", "decor"; "place-pillar", "pillar"
      "place-hatch", "hatch"; "place-exit", "exit" ]

let private selectBrush (mode: string) =
    placeMode <- mode
    for id, m in brushButtons do
        let el = byId id
        if not (isNull el) then
            el?classList?toggle ("selected", m = mode) |> ignore

for id, mode in brushButtons do
    let el = byId id
    if not (isNull el) then
        el.addEventListener ("click", fun _ -> selectBrush mode)

// ---- Import image -> terrain (dark, opaque pixels become solid) -------------
let private thresholdImage (img: HTMLImageElement) =
    let w = max 240 (min 1200 (int (float img.width * float H / float img.height)))
    W <- w
    let off = document.createElement "canvas" :?> HTMLCanvasElement
    off.width <- float w
    off.height <- float H
    let octx = off.getContext_2d ()
    octx?drawImage (img, 0.0, 0.0, float w, float H)
    let id = octx.getImageData (0.0, 0.0, float w, float H)
    let d: byte[] = unbox id.data
    let t = Array.zeroCreate (w * H)

    for i in 0 .. w * H - 1 do
        let r = float d.[i * 4]
        let g = float d.[i * 4 + 1]
        let b = float d.[i * 4 + 2]
        let a = d.[i * 4 + 3]
        let lum = 0.299 * r + 0.587 * g + 0.114 * b
        t.[i] <- a > 128uy && lum < 128.0

    terrain <- t
    steel <- Array.zeroCreate (w * H)
    lava <- Array.zeroCreate (w * H)
    water <- Array.zeroCreate (w * H)
    decor <- [] // stale coordinates would float over the new terrain
    pillars <- []
    redraw ()

(byId "img").addEventListener (
    "change",
    fun _ ->
        let input = byId "img" :?> HTMLInputElement
        if input.files.length > 0 then
            let reader = FileReader.Create()
            reader.onload <-
                fun _ ->
                    let img = document.createElement "img" :?> HTMLImageElement
                    img.addEventListener ("load", fun _ -> thresholdImage img)
                    img.src <- unbox reader.result
            reader.readAsDataURL input.files.[0]
)

// ---- Export -----------------------------------------------------------------
let private valOf (id: string) = (byId id :?> HTMLInputElement).value
let private parseIntOr (d: int) (s: string) = try int s with _ -> d

let private skillInputs =
    [ "inv-digger", Digger
      "inv-basher", Basher
      "inv-miner", Miner
      "inv-builder", Builder
      "inv-blocker", Blocker
      "inv-climber", Climber
      "inv-floater", Floater
      "inv-bomber", Bomber ]

let private themeOf =
    function
    | "stone" -> Stone
    | "brick" -> Brick
    | "hell" -> Hell
    | _ -> Earth

let private buildLevel () : Level =
    { Name = valOf "f-name"
      Theme = themeOf (valOf "f-theme")
      Width = W
      Height = H
      Terrain = terrain
      Steel = steel
      Lava = lava
      Water = water
      Hatch = hatch
      Exit = exit
      Decor = decor
      Pillars = pillars
      SpawnCount = parseIntOr 10 (valOf "f-spawn")
      SpawnEveryTicks = parseIntOr 14 (valOf "f-every")
      SaveTarget = parseIntOr 5 (valOf "f-save")
      TimeLimitTicks = parseIntOr 1500 (valOf "f-time")
      Inventory =
        skillInputs
        |> List.choose (fun (id, s) ->
            let n = parseIntOr 0 (valOf id)
            if n > 0 then Some(s, n) else None)
        |> Map.ofList }

let private download (name: string) (content: string) =
    let a = document.createElement "a" :?> HTMLAnchorElement
    a.href <- "data:application/json;charset=utf-8," + JS.encodeURIComponent content
    a.setAttribute ("download", name)
    a.click ()

(byId "export").addEventListener (
    "click",
    fun _ ->
        let json = LevelJson.encodeCampaign [ buildLevel () ]
        (byId "out" :?> HTMLTextAreaElement).value <- json
        download "levels.json" json
)

// Save into browser-local storage; the game picks these up on load.
(byId "save").addEventListener (
    "click",
    fun _ ->
        Storage.addUserLevel (buildLevel ())
        (byId "save-status").textContent <-
            sprintf "Saved — you now have %d browser level(s). Open the game to play them." (Storage.count ())
)

// ---- Init -------------------------------------------------------------------
terrain <- groundTerrain W
selectBrush "dirt"
redraw ()
