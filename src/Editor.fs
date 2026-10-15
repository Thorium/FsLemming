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

[<Literal>]
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

// The last Check result, drawn over the map until the next edit.
let mutable private lastReport: LevelCheck.Report option = None

// The check's search runs in a Web Worker (CheckWorker.fs). Each check gets a
// number; an edit or a new check stops the running worker, and a reply for an
// older number is dropped.
let mutable private checkRun = 0
let mutable private checkWorker: obj option = None

[<Emit("new Worker(new URL('./CheckWorker.fs.js', import.meta.url), { type: 'module' })")>]
let private newCheckWorker () : obj = jsNative

let private stopCheck () =
    checkRun <- checkRun + 1
    match checkWorker with
    | Some w ->
        w?terminate ()
        checkWorker <- None
    | None -> ()

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

    // The Check: the no-skill walk (grey), the solution (yellow) with a labelled
    // dot where each skill is given, a red dot where the walk ends badly, and a
    // magenta cross on each problem spot.
    match lastReport with
    | None -> ()
    | Some r ->
        let px (x: int) = (float x + 0.5) * scaleX
        let py (y: int) = (float y + 0.5) * scaleY
        let trace (points: (int * int) list) (colour: string) =
            ctx?fillStyle <- colour
            for x, y in points do
                ctx.fillRect (px x - 1.0, py y - 1.0, 2.0, 2.0)
        let spot (x: int) (y: int) (colour: string) =
            ctx?fillStyle <- colour
            ctx.beginPath ()
            ctx.arc (px x, py y, 4.0, 0.0, 2.0 * System.Math.PI)
            ctx.fill ()
        trace r.NoSkills.Trace "rgba(190, 190, 190, 0.8)"
        match r.Solution with
        | Some s ->
            trace s.Trace "rgba(255, 230, 60, 0.95)"
            ctx.font <- "9px monospace"
            ctx?textAlign <- "center"
            ctx?textBaseline <- "bottom"
            for a in s.Assignments do
                spot a.X a.Y "#50dcff"
                ctx?fillStyle <- "#ffffff"
                ctx.fillText ($"%A{a.Skill}", px a.X, py a.Y - 5.0)
        | None -> ()
        if r.NoSkills.End <> LevelCheck.Saved then spot r.NoSkills.EndX r.NoSkills.EndY "#ff3030"
        ctx?strokeStyle <- "#ff40ff"
        ctx.lineWidth <- 2.0
        for f in r.Findings do
            if f.X >= 0 && f.Severity <> LevelCheck.Note then
                ctx.beginPath ()
                ctx.moveTo (px f.X - 6.0, py f.Y - 6.0)
                ctx.lineTo (px f.X + 6.0, py f.Y + 6.0)
                ctx.moveTo (px f.X + 6.0, py f.Y - 6.0)
                ctx.lineTo (px f.X - 6.0, py f.Y + 6.0)
                ctx.stroke ()

// ---- Editing: draw/erase terrain, place hatch/exit (click or drag) ----------
let mutable private painting = false

/// The map changed: the last Check no longer applies.
let private staleCheck () =
    let running = checkWorker.IsSome
    stopCheck ()
    if lastReport.IsSome || running then
        lastReport <- None
        (byId "check-findings").innerHTML <- ""
        (byId "check-status").textContent <- "Edited since the check: check again."

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
    // Whole records, not { hatch with ... }: Fable copies a mutable struct through a
    // reference cell there and loses W and H.
    | "hatch" -> hatch <- { X = wx - hatch.W / 2; Y = wy - hatch.H / 2; W = hatch.W; H = hatch.H }
    | "exit" -> exit <- { X = wx - exit.W / 2; Y = wy - exit.H / 2; W = exit.W; H = exit.H }
    | _ -> ()

    staleCheck ()
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
    staleCheck ()
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

// ---- Check & quick fixes ----------------------------------------------------
// LevelCheck runs one lemming with the game's own rules (Lemming.step,
// World.applyMutation) and searches for a solution; the findings list below
// the buttons and the overlay on the map show what it found.

let private escapeHtml (s: string) = s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")

let private showReport (r: LevelCheck.Report) =
    let sevName =
        function
        | LevelCheck.Problem -> "problem"
        | LevelCheck.Warning -> "warning"
        | LevelCheck.Note -> "note"
    (byId "check-findings").innerHTML <-
        r.Findings
        |> List.map (fun f ->
            let spot = if f.X >= 0 then $" <small>(%d{f.X}, %d{f.Y})</small>" else ""
            sprintf "<li class=\"%s\"><span class=\"sev\">%s</span>%s%s</li>" (sevName f.Severity) (sevName f.Severity) (escapeHtml f.Message) spot)
        |> String.concat ""
    let count sev = r.Findings |> List.filter (fun f -> f.Severity = sev) |> List.length
    (byId "check-status").textContent <-
        match count LevelCheck.Problem, count LevelCheck.Warning with
        | 0, 0 -> "Looks good."
        | p, wn -> $"%d{p} problem(s), %d{wn} warning(s)."

/// Run the check: the search in a Web Worker (it can take seconds), the rest
/// here once it replies.
let private runCheck () =
    stopCheck ()
    let run = checkRun
    let level = buildLevel ()
    lastReport <- None
    (byId "check-findings").innerHTML <- ""
    (byId "check-status").textContent <- "Checking…"
    redraw ()
    let worker = newCheckWorker ()
    checkWorker <- Some worker
    let finish () =
        worker?terminate ()
        checkWorker <- None
    worker?onmessage <- fun (e: obj) ->
        if run = checkRun then
            finish ()
            let reply = e?data
            if isNull reply then
                (byId "check-status").textContent <- "The check could not read this level."
            else
                let plan = if reply?found then Some(LevelCheck.planOfInts reply?plan) else None
                let r = LevelCheck.checkWith (fun _ -> plan, reply?complete) level
                lastReport <- Some r
                showReport r
                redraw ()
    worker?onerror <- fun (_: obj) ->
        if run = checkRun then
            finish ()
            (byId "check-status").textContent <- "The check failed: see the browser console."
    worker?postMessage (LevelJson.encodeCampaign [ level ])

// Changing the numbers or the inventory also makes the last check stale.
for id in [ "f-spawn"; "f-every"; "f-save"; "f-time"; "inv-digger"; "inv-basher"; "inv-miner"; "inv-builder"; "inv-blocker"; "inv-climber"; "inv-floater"; "inv-bomber" ] do
    (byId id).addEventListener ("input", fun _ -> staleCheck ())

let private solidAt x y = x >= 0 && x < W && y >= 0 && y < H && terrain.[y * W + x]

/// The ground surface in column x from row y: the first solid cell below with
/// open air above it — or, when (x, y) is inside terrain, the surface above.
let private surfaceFrom (x: int) (y: int) =
    if solidAt x y then
        let rec up yy = if yy <= 0 then None elif not (solidAt x (yy - 1)) then Some yy else up (yy - 1)
        up y
    else
        [ max 0 (y + 1) .. H - 1 ] |> List.tryFind (fun yy -> solidAt x yy)

(byId "check").addEventListener ("click", fun _ -> runCheck ())

// Stand the exit on the ground under (or above) its middle.
(byId "fix-exit").addEventListener ("click", fun _ ->
    match surfaceFrom (exit.X + exit.W / 2) (exit.Y + exit.H - 1) with
    | Some ground ->
        exit <- { X = exit.X; Y = ground - exit.H; W = exit.W; H = exit.H }
        redraw ()
        runCheck ()
    | None -> (byId "check-status").textContent <- "There is no ground under the exit to stand it on.")

// Put the hatch at half the safe fall above the ground below it.
(byId "fix-hatch").addEventListener ("click", fun _ ->
    match surfaceFrom (hatch.X + hatch.W / 2) hatch.Y with
    | Some ground ->
        hatch <- { X = hatch.X; Y = max 0 (ground - 1 - Lemming.safeFall / 2); W = hatch.W; H = hatch.H }
        redraw ()
        runCheck ()
    | None -> (byId "check-status").textContent <- "There is no ground under the hatch: draw some first.")

// ---- Init -------------------------------------------------------------------
terrain <- groundTerrain W
selectBrush "dirt"
redraw ()
