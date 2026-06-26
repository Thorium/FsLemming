module FsLemming.Render

open Browser.Types
open Fable.Core.JsInterop
open FsLemming.Types

/// A dumb renderer. It only ever sees immutable snapshots — never an actor's
/// private state. All knowledge of *what a lemming looks like* lives in the
/// Sprites module. It draws a `viewW`-wide horizontal window of the world
/// starting at `camX` (the camera); world is only as wide as the level.

// Presentation-only animation clock. Lives here, not in the domain.
let mutable private clock = 0

// Reused ImageData buffers (main view + minimap), recreated only on size change.
let mutable private buffer: ImageData option = None
let mutable private miniBuf: ImageData option = None

/// Per-theme terrain palette: (top surface, body A, body B, sky/background).
let private themeColors theme =
    match theme with
    | Earth -> (72uy, 164uy, 56uy), (96uy, 64uy, 32uy), (120uy, 80uy, 44uy), (20uy, 20uy, 35uy)
    | Stone -> (212uy, 180uy, 92uy), (168uy, 128uy, 52uy), (192uy, 150uy, 70uy), (28uy, 22uy, 38uy)
    | Brick -> (152uy, 152uy, 162uy), (92uy, 92uy, 108uy), (114uy, 114uy, 130uy), (22uy, 22uy, 30uy)
    | Hell -> (210uy, 64uy, 30uy), (70uy, 34uy, 34uy), (92uy, 46uy, 42uy), (14uy, 8uy, 12uy)

// Steel reads as cold metallic grey-blue (with a lighter top), distinct from any
// theme's dirt so players can see what's indestructible.
let private steelTop, steelBody = (176uy, 184uy, 200uy), (104uy, 112uy, 130uy)

// Hazard fills, painted into the pixel buffer so solid terrain occludes them —
// the pools then read as recessed pits, not slabs sitting on top of the ground.
let private lavaBody, lavaTop = (216uy, 51uy, 15uy), (255uy, 154uy, 51uy)
let private waterBody, waterTop = (42uy, 108uy, 208uy), (111uy, 179uy, 255uy)

/// The hazard colour for an empty cell (brighter at the exposed pool surface —
/// where the cell above is not the same hazard), or None.
let private hazardColorAt (t: TerrainSnapshot) wx wy =
    let i = wy * t.Width + wx
    let aboveSame (mask: bool[]) = wy > 0 && mask.[i - t.Width]
    if t.Lava.[i] then Some(if aboveSame t.Lava then lavaBody else lavaTop)
    elif t.Water.[i] then Some(if aboveSame t.Water then waterBody else waterTop)
    else None

let private paint (data: byte[]) (p: int) (r: byte, g: byte, b: byte) =
    data.[p] <- r
    data.[p + 1] <- g
    data.[p + 2] <- b
    data.[p + 3] <- 255uy

let draw
    (ctx: CanvasRenderingContext2D)
    (terrain: TerrainSnapshot)
    (views: LemmingView list)
    (level: Level)
    (camX: int)
    (viewW: int)
    (openProgress: float)
    =
    clock <- clock + 1
    let h = terrain.Height
    let tw = terrain.Width
    let topC, bodyA, bodyB, skyC = themeColors level.Theme

    let img =
        match buffer with
        | Some b when int b.width = viewW && int b.height = h -> b
        | _ ->
            let b = ctx.createImageData (float viewW, float h)
            buffer <- Some b
            b

    let data: byte[] = unbox img.data

    // Terrain: only the visible window [camX, camX+viewW). Grass on the top
    // surface, dithered earth below; empty cells show sky, or a hazard pool where
    // one has been carved into the ground (so the terrain occludes its edges).
    for vy in 0 .. h - 1 do
        for vx in 0 .. viewW - 1 do
            let wx = camX + vx
            let solid = wx >= 0 && wx < tw && terrain.Solid.[vy * tw + wx]

            let color =
                if solid then
                    let exposedTop = vy = 0 || not terrain.Solid.[(vy - 1) * tw + wx]

                    if wx >= 0 && wx < tw && terrain.Steel.[vy * tw + wx] then
                        if exposedTop then steelTop else steelBody // indestructible
                    elif exposedTop then
                        topC
                    elif level.Theme = Brick && vy % 4 = 0 then bodyA // mortar line
                    elif (wx + vy) % 2 = 0 then bodyA
                    else bodyB
                else
                    match hazardColorAt terrain wx vy with
                    | Some c -> c
                    | None -> skyC

            paint data ((vy * viewW + vx) * 4) color

    ctx.putImageData (img, 0., 0.)

    // Hatch (the level-start trapdoor) and exit (a little door), offset by camera.
    // The hatch is drawn at 2x its logical size — centred horizontally on the spawn
    // region — so the entrance reads clearly; lemmings still drop from that region.
    let hatch = level.Hatch
    let exit = level.Exit
    let hscale = 2.0
    let hw = float hatch.W * hscale
    let hh = float hatch.H * hscale
    let hx = float (hatch.X - camX) - (hw - float hatch.W) / 2.0
    let hy = float hatch.Y
    ctx?fillStyle <- "#7a4a1e"
    ctx.fillRect (hx - 1.0, hy - 1.0, hw + 2.0, 2.0) // lintel
    ctx?fillStyle <- "#160e1c"
    ctx.fillRect (hx, hy + 1.0, hw, hh) // opening
    // Two doors that slide apart as the gate opens (openProgress 0 -> 1).
    let doorW = (hw / 2.0) * (1.0 - openProgress)
    ctx?fillStyle <- "#9a5a2a"
    ctx.fillRect (hx, hy + 1.0, doorW, hh)
    ctx.fillRect (hx + hw - doorW, hy + 1.0, doorW, hh)

    let ex = float (exit.X - camX)
    ctx?fillStyle <- "#0c6e2c"
    ctx.fillRect (ex - 1.0, float exit.Y - 2.0, float exit.W + 2.0, float exit.H + 2.0) // frame
    ctx?fillStyle <- "#19d152"
    ctx.fillRect (ex, float exit.Y, float exit.W, 2.0) // glowing lintel
    ctx?fillStyle <- "#0a1a0e"
    ctx.fillRect (ex + 2.0, float exit.Y + 2.0, float exit.W - 4.0, float exit.H - 2.0) // doorway

    // Lemmings as sprites, culled to the window.
    if Sprites.loaded then
        let fs = float Sprites.frameSize

        for v in views do
            if v.Alive then
                let dx = float (v.X - camX) - fs / 2.0

                if dx > -fs && dx < float viewW then
                    let climbing = v.Skill = Climber && terrain.IsSolid(v.X + v.Dir, v.Y)
                    // Only a real fall (more than a small step) shows the umbrella /
                    // tumble; stepping down a bump keeps the walk frame.
                    let showFall = not (terrain.IsSolid(v.X, v.Y + 1)) && v.FallDist > 3
                    // Free-running animation tick; per-lemming offset so they don't
                    // all step in lockstep. ~3 ticks/frame keeps the march brisk.
                    let anim = clock / 3 + v.Id
                    let sx = float (Sprites.frameColumn v.Skill showFall climbing anim) * fs
                    let sy = float (v.Id % Sprites.variantCount) * fs
                    let dy = float v.Y - fs + 2.0

                    if v.Dir >= 0 then
                        ctx?drawImage (Sprites.image, sx, sy, fs, fs, dx, dy, fs, fs)
                    else
                        ctx.save ()
                        ctx.translate (dx + fs, dy)
                        ctx.scale (-1.0, 1.0)
                        ctx?drawImage (Sprites.image, sx, sy, fs, fs, 0.0, 0.0, fs, fs)
                        ctx.restore ()

                    // A counting-down bomber shows a tiny 5..1 above its head.
                    if v.Fuse > 0 then
                        ctx?fillStyle <- "#ffcc33"
                        ctx?textAlign <- "center"
                        ctx.font <- "6px monospace"
                        ctx.fillText (string ((v.Fuse + 7) / 8), float (v.X - camX), float v.Y - fs + 1.0)

/// The whole level scaled into a small map, with lemming dots and a rectangle
/// marking the visible viewport. Stretched to fill (aspect not preserved).
let drawMinimap
    (ctx: CanvasRenderingContext2D)
    (terrain: TerrainSnapshot)
    (views: LemmingView list)
    (level: Level)
    (camX: int)
    (viewW: int)
    (mw: int)
    (mh: int)
    =
    let tw = terrain.Width
    let th = terrain.Height
    let _, solidC, _, skyC = themeColors level.Theme

    let img =
        match miniBuf with
        | Some b when int b.width = mw && int b.height = mh -> b
        | _ ->
            let b = ctx.createImageData (float mw, float mh)
            miniBuf <- Some b
            b

    let data: byte[] = unbox img.data

    for my in 0 .. mh - 1 do
        let wy = my * th / mh
        for mx in 0 .. mw - 1 do
            let wx = mx * tw / mw

            let color =
                if terrain.Solid.[wy * tw + wx] then
                    if terrain.Steel.[wy * tw + wx] then steelBody else solidC
                else
                    match hazardColorAt terrain wx wy with
                    | Some c -> c
                    | None -> skyC

            paint data ((my * mw + mx) * 4) color

    ctx.putImageData (img, 0., 0.)

    ctx?fillStyle <- "#ffdd00"

    for v in views do
        if v.Alive then
            ctx.fillRect (float (v.X * mw / tw), float (v.Y * mh / th), 1.0, 1.0)

    // Start (hatch) and end (exit), each a small colour blob centred on its region.
    let marker (r: Region) color =
        ctx?fillStyle <- color
        let cx = (r.X + r.W / 2) * mw / tw
        let cy = (r.Y + r.H / 2) * mh / th
        ctx.fillRect (float cx, float cy, 2.0, 2.0)

    marker level.Hatch "#33ff66" // start
    marker level.Exit "#ff3344" // end

    ctx?strokeStyle <- "#ffffff"
    ctx.strokeRect (float (camX * mw / tw), 0.5, float (viewW * mw / tw), float mh - 1.0)
