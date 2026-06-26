module FsLemming.App

open Browser
open Browser.Types
open Fable.Core.JsInterop
open FsLemming.Types
open FsLemming.Game
open FsLemming.Levels

// ---- DOM --------------------------------------------------------------------
let private canvas = document.getElementById "game" :?> HTMLCanvasElement
let private ctx = canvas.getContext_2d ()
let private miniCanvas = document.getElementById "map" :?> HTMLCanvasElement
let private miniCtx = miniCanvas.getContext_2d ()
let private byId (id: string) = document.getElementById id

// ---- Campaign + current game ------------------------------------------------
// Built-in campaign plus any levels the user saved in this browser (editor).
let mutable private levels = campaign @ Storage.loadUserLevels ()
let mutable private levelIndex = 0

// Lemming-count multiplier (×1 / ×2). Scaling both spawn and save keeps the goal
// proportional. JS handles far more than this without trouble.
let mutable private mult = 1

let private scaledLevel (lvl: Level) =
    { lvl with
        SpawnCount = lvl.SpawnCount * mult
        SaveTarget = lvl.SaveTarget * mult
        // Scale the skill allowance too, so e.g. 20 lemmings get 20 parachutes.
        Inventory = lvl.Inventory |> Map.map (fun _ n -> n * mult) }

let mutable private game = Game(scaledLevel levels.[0])
let mutable private lastViews: LemmingView list = []
let mutable private selected = Floater // the parachute — the first level's lesson
// Transient click feedback: world (x, y), succeeded?, frames remaining.
let mutable private clickFx: (int * int * bool * int) option = None
let private fxLife = 5 // ticks the click ripple lasts (lower = snappier)

// ---- Camera (horizontal scroll for levels wider than the viewport) ----------
let mutable private camX = 0
let mutable private scrollDir = 0 // -1 / 0 / +1, set by edge-hover
let mutable private paused = false
let mutable private miniDragging = false // dragging the minimap
let mutable private panning = false // right-button drag on the main canvas
let mutable private panLastX = 0.0

let private clampCam x =
    let maxX = max 0 (game.Level.Width - int canvas.width)
    max 0 (min x maxX)

// Skill buttons: (element id, skill, display label).
let private skillButtons =
    [ "skill-digger", Digger, "Digger"
      "skill-basher", Basher, "Basher"
      "skill-miner", Miner, "Miner"
      "skill-builder", Builder, "Builder"
      "skill-blocker", Blocker, "Blocker"
      "skill-climber", Climber, "Climber"
      "skill-floater", Floater, "Floater"
      "skill-bomber", Bomber, "Bomber" ]

// Outline the button for the currently selected skill.
let private highlight () =
    for id, skill, _ in skillButtons do
        let cls = (byId id).classList
        if skill = selected then cls.add "selected" else cls.remove "selected"

// Repaint each skill button label with its remaining count, e.g. "Digger (2)".
let private refreshCounts () =
    async {
        let! counts = game.Inventory.Remaining()
        for id, skill, label in skillButtons do
            let n = counts |> Map.tryFind skill |> Option.defaultValue 0
            // Update the label span, not the button — that keeps the icon child.
            ((byId id).querySelector ".n").textContent <- sprintf "%s (%d)" label n
    }

// ---- HUD --------------------------------------------------------------------
let private setText (id: string) (text: string) = (byId id).textContent <- text

let private updateHud (status: GameStatus) =
    let lvl = game.Level
    setText "hud-level" lvl.Name
    setText "hud-saved" (sprintf "Saved %d / %d" status.Saved lvl.SaveTarget)
    setText "hud-out" (sprintf "Out %d / %d" status.Spawned lvl.SpawnCount)
    setText "hud-time" (sprintf "Time %d" (max 0 (lvl.TimeLimitTicks - status.Elapsed)))

    setText
        "hud-status"
        (match status.Outcome with
         | Playing -> ""
         | Won -> "LEVEL COMPLETE — click Next"
         | Lost -> "FAILED — click Retry")

// ---- Level loading ----------------------------------------------------------
let private loadLevel i =
    game.StopAll() // shut down the previous level's lemming agents
    levelIndex <- max 0 (min i (List.length levels - 1))
    game <- Game(scaledLevel levels.[levelIndex])
    camX <- 0
    (byId "level-select" :?> HTMLSelectElement).selectedIndex <- levelIndex
    // Keep the URL shareable: ?level=<index>.
    window.history.replaceState (null, "", "?level=" + string levelIndex)
    refreshCounts () |> Async.StartImmediate

// Fill the level dropdown from the current `levels` list.
let private populateLevelSelect () =
    let sel = byId "level-select" :?> HTMLSelectElement
    sel.innerHTML <- ""
    levels
    |> List.iteri (fun i lvl ->
        let opt = document.createElement "option" :?> HTMLOptionElement
        opt.value <- string i
        opt.textContent <- lvl.Name
        sel.appendChild opt |> ignore)
    sel.selectedIndex <- levelIndex

// ---- Wiring: skills, campaign buttons, nuke, canvas clicks ------------------
skillButtons
|> List.iter (fun (id, skill, _) ->
    (byId id).addEventListener("click", fun _ ->
        selected <- skill
        highlight ()))

// Nuke detonates everyone now; each lemming shows its own fuse counter.
(byId "nuke").addEventListener ("click", fun _ -> game.NukeAll())
(byId "retry").addEventListener ("click", fun _ -> loadLevel levelIndex)
(byId "next").addEventListener ("click", fun _ -> loadLevel (levelIndex + 1))

(byId "count-1x").addEventListener ("click", fun _ -> mult <- 1; loadLevel levelIndex)
(byId "count-2x").addEventListener ("click", fun _ -> mult <- 2; loadLevel levelIndex)

let private togglePause () =
    paused <- not paused
    (byId "pause").textContent <- (if paused then "▶ Resume" else "⏸ Pause")

(byId "pause").addEventListener ("click", fun _ -> togglePause ())

(byId "level-select").addEventListener (
    "change",
    fun _ -> loadLevel (int (byId "level-select" :?> HTMLSelectElement).value)
)

// Upload a levels.json (from disk) and append it to the playable set.
(byId "load-file").addEventListener (
    "change",
    fun _ ->
        let input = byId "load-file" :?> HTMLInputElement
        if input.files.length > 0 then
            let reader = FileReader.Create()
            reader.onload <-
                fun _ ->
                    let loaded =
                        try
                            LevelJson.decodeCampaignJson (unbox reader.result)
                        with _ ->
                            []
                    if not (List.isEmpty loaded) then
                        let startIndex = List.length levels
                        levels <- levels @ loaded
                        populateLevelSelect ()
                        loadLevel startIndex
            reader.readAsText input.files.[0]
)

canvas.addEventListener (
    "click",
    fun e ->
        let me = e :?> MouseEvent
        let rect = canvas.getBoundingClientRect ()
        // Canvas is CSS-scaled; map the click back to WORLD pixels (add camera).
        let px = (me.clientX - rect.left) / rect.width * float canvas.width + float camX
        let py = (me.clientY - rect.top) / rect.height * float canvas.height

        let nearest =
            lastViews
            |> List.filter (fun v -> v.Alive)
            |> List.sortBy (fun v -> (float v.X - px) ** 2.0 + (float v.Y - py) ** 2.0)
            |> List.tryHead

        match nearest with
        | Some v ->
            // Spend one from the inventory; only assign if one was in stock.
            async {
                let! granted = game.TryAssign(v.Id, selected)
                // Ripple on the lemming: green if applied, red if none left.
                clickFx <- Some(v.X, v.Y, granted, fxLife)
                if granted then do! refreshCounts ()
            }
            |> Async.StartImmediate
        | None ->
            // Clicked empty space — red ripple at the click point.
            clickFx <- Some(int px, int py, false, fxLife)
)

// ---- Scrolling: edge-hover, arrow keys, click-to-jump on the minimap --------
canvas.addEventListener (
    "mousemove",
    fun e ->
        let me = e :?> MouseEvent
        let rect = canvas.getBoundingClientRect ()
        let cx = (me.clientX - rect.left) / rect.width * float canvas.width
        scrollDir <- if cx < 30.0 then -1 elif cx > float canvas.width - 30.0 then 1 else 0
)

canvas.addEventListener ("mouseleave", fun _ -> scrollDir <- 0)

window.addEventListener (
    "keydown",
    fun e ->
        match (e :?> KeyboardEvent).key with
        | "ArrowLeft" -> camX <- clampCam (camX - 12)
        | "ArrowRight" -> camX <- clampCam (camX + 12)
        | "p" | "P" -> togglePause ()
        | _ -> ()
)

// Minimap: click or drag to move the camera (centre the view on the cursor).
let private miniJumpTo (clientX: float) =
    let rect = miniCanvas.getBoundingClientRect ()
    let mx = (clientX - rect.left) / rect.width * float miniCanvas.width
    let worldCenter = mx * float game.Level.Width / float miniCanvas.width
    camX <- clampCam (int worldCenter - int canvas.width / 2)

miniCanvas.addEventListener ("mousedown", fun e ->
    miniDragging <- true
    miniJumpTo (e :?> MouseEvent).clientX)

// Main canvas: right-button drag to pan (left click stays skill-assignment).
canvas.addEventListener ("contextmenu", fun e -> e.preventDefault ())

canvas.addEventListener ("mousedown", fun e ->
    let me = e :?> MouseEvent
    if me.button = 2 then
        panning <- true
        panLastX <- me.clientX)

// Drag bookkeeping lives on the window so it keeps tracking outside the element.
window.addEventListener ("mousemove", fun e ->
    let me = e :?> MouseEvent
    if miniDragging then
        miniJumpTo me.clientX
    elif panning then
        let rect = canvas.getBoundingClientRect ()
        let dxWorld = (me.clientX - panLastX) / rect.width * float canvas.width
        panLastX <- me.clientX
        camX <- clampCam (camX - int dxWorld)) // grab-and-drag

window.addEventListener ("mouseup", fun _ ->
    miniDragging <- false
    panning <- false)

// ---- Speed control ----------------------------------------------------------
type private Speed =
    | Slow
    | Normal
    | Fast
    | Turbo

let private intervalFor =
    function
    | Slow -> 120
    | Normal -> 60
    | Fast -> 30
    | Turbo -> 8

let mutable private speed = Normal

[ "speed-slow", Slow; "speed-normal", Normal; "speed-fast", Fast; "speed-turbo", Turbo ]
|> List.iter (fun (id, sp) -> (byId id).addEventListener("click", fun _ -> speed <- sp))

// ---- Explosion particles ----------------------------------------------------
type private Particle =
    { X: float; Y: float; Vx: float; Vy: float; Life: int; Color: string }

let private palette = [| "#16c60c"; "#2b3bd6"; "#ffd9a0"; "#aa6633" |]
let mutable private particles: Particle list = []

// A radial burst of 12 pixels flying outward (deterministic — no RNG needed).
let private burstAt (ex: int, ey: int) =
    for i in 0..11 do
        let a = float i / 12.0 * System.Math.PI * 2.0
        let speed = 1.1 + 0.35 * float (i % 3)
        particles <-
            { X = float ex
              Y = float ey
              Vx = cos a * speed
              Vy = sin a * speed - 0.8 // slight upward kick
              Life = 26
              Color = palette.[i % palette.Length] }
            :: particles

let private updateParticles () =
    particles <-
        particles
        |> List.choose (fun p ->
            let life = p.Life - 1
            if life <= 0 then
                None
            else
                Some { p with X = p.X + p.Vx; Y = p.Y + p.Vy; Vy = p.Vy + 0.12; Life = life }) // gravity

// A small low reddish puff when a lemming splats from a fatal fall.
let private splatAt (ex: int, ey: int) =
    for i in 0..5 do
        let a = float i / 6.0 * System.Math.PI * 2.0
        particles <-
            { X = float ex
              Y = float ey
              Vx = cos a * 0.7
              Vy = -0.5 - 0.25 * float (i % 2)
              Life = 16
              Color = (if i % 2 = 0 then "#cc3333" else "#ffd9a0") }
            :: particles

// Lava death: orange/yellow flames flying up.
let private burnAt (ex: int, ey: int) =
    for i in 0..7 do
        let a = float i / 8.0 * System.Math.PI * 2.0
        particles <-
            { X = float ex
              Y = float ey
              Vx = cos a * 0.6
              Vy = -0.9 - 0.3 * float (i % 3)
              Life = 22
              Color = (if i % 2 = 0 then "#ff7711" else "#ffcc33") }
            :: particles

// Water death: a few blue bubbles rising.
let private drownAt (ex: int, ey: int) =
    for i in 0..5 do
        particles <-
            { X = float ex + float (i - 3)
              Y = float ey
              Vx = 0.0
              Vy = -0.5 - 0.1 * float (i % 2)
              Life = 18
              Color = "#66bbff" }
            :: particles

let private spawnEffect (x: int, y: int, fx: EffectKind) =
    match fx with
    | Explode -> burstAt (x, y)
    | Splat -> splatAt (x, y)
    | Burn -> burnAt (x, y)
    | Drown -> drownAt (x, y)

let private drawParticles () =
    for p in particles do
        ctx?fillStyle <- p.Color
        ctx.fillRect (p.X - float camX, p.Y, 2.0, 2.0)

// ---- Game loop --------------------------------------------------------------
// Self-scheduling loop (see notes in earlier steps): one tick, then reschedule.
let rec private tickLoop () =
    async {
        try
            // While paused we skip the whole tick — the last frame stays on screen.
            if not paused then
                let! terrain, views, effects, status = game.Tick()
                lastViews <- views
                camX <- clampCam (camX + scrollDir * 4) // apply edge-hover scrolling

                for e in effects do
                    spawnEffect e

                updateParticles ()

                Render.draw ctx terrain views game.Level camX (int canvas.width) status.OpenProgress
                Render.drawMinimap miniCtx terrain views game.Level camX (int canvas.width) (int miniCanvas.width) (int miniCanvas.height)
                drawParticles ()

                // Expanding click ripple (green = skill applied, red = missed / none left).
                match clickFx with
                | Some(x, y, ok, ttl) when ttl > 0 ->
                    ctx?strokeStyle <- (if ok then "#19d152" else "#ff5555")
                    ctx.lineWidth <- 2.0
                    ctx.beginPath ()
                    ctx.arc (float (x - camX), float y, float (fxLife - ttl) * 3.0 + 2.0, 0.0, 6.2832)
                    ctx.stroke ()
                    clickFx <- Some(x, y, ok, ttl - 1)
                | _ -> clickFx <- None

                updateHud status
        with ex ->
            console.error ("FsLemming tick failed: " + string ex)

        window.setTimeout((fun _ -> tickLoop () |> Async.StartImmediate), intervalFor speed)
        |> ignore
    }

// Read ?level=N from the URL (so a level is linkable / bookmarkable).
let private queryInt (key: string) : int option =
    let s = window.location.search
    let s = if s.StartsWith "?" then s.Substring 1 else s
    s.Split('&')
    |> Array.tryPick (fun kv ->
        match kv.Split('=') with
        | [| k; v |] when k = key -> (try Some(int v) with _ -> None)
        | _ -> None)

populateLevelSelect ()
highlight ()
loadLevel (defaultArg (queryInt "level") 0) // honours ?level=N; also refreshes counts
tickLoop () |> Async.StartImmediate
