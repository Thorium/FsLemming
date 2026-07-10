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

/// Deterministic coordinate hash for the procedural sky backdrop. Stable frame
/// to frame (no shimmer) and derived purely from position, so it needs no level
/// data at all. Constants kept small on purpose: Fable ints are JS numbers, and
/// huge multiplies would lose exactness.
let inline private hash (x: int) (y: int) =
    let h = x * 92821 + y * 31337
    let h = h ^^^ (h >>> 7)
    let h = h * 2999
    abs (h ^^^ (h >>> 11))

let private lighten (r: byte, g: byte, b: byte) k =
    byte (min 255 (int r + k)), byte (min 255 (int g + k)), byte (min 255 (int b + k))

let private darken (r: byte, g: byte, b: byte) k =
    byte (max 0 (int r - k)), byte (max 0 (int g - k)), byte (max 0 (int b - k))

/// Faint background material hanging in the sky, in the spirit of the original
/// game's backdrops: roots on earth, ruined pillars on stone, chains on brick,
/// stalactites in hell. Callers paint hits a couple of shades above the sky
/// colour so it stays firmly backdrop and never competes with the play field.
let private skyDetail theme (h: int) wx wy =
    match theme with
    | Earth -> // hanging roots of uneven length, swaying a pixel with depth
        let band = wx / 6
        hash band 7 % 3 = 0
        && wy < 10 + hash band 13 % 22
        && wx % 6 = 2 + hash band (wy / 7) % 2
    | Stone -> // distant ruined pillars, floor to ceiling, with capital and base
        let band = wx / 44
        hash band 3 % 2 = 0
        && (let dx = abs (wx - (band * 44 + 10 + hash band 5 % 24))
            dx <= 2 || (dx <= 4 && (wy < 4 || wy >= h - 4)))
    | Brick -> // hanging chains, dotted so the links read
        let band = wx / 24
        hash band 9 % 2 = 0
        && wx = band * 24 + 2 + hash band 11 % 20
        && wy < 12 + hash band 15 % 20
        && wy % 3 <> 2
    | Hell -> // stalactites on the cave ceiling
        let band = wx / 12
        let cx = band * 12 + 3 + hash band 17 % 6
        wy < 5 + hash band 19 % 14 - abs (wx - cx) * 3

// Hazard fills, painted into the pixel buffer so solid terrain occludes them —
// the pools then read as recessed pits, not slabs sitting on top of the ground.
let private lavaBody, lavaTop = (216uy, 51uy, 15uy), (255uy, 154uy, 51uy)
let private waterBody, waterTop = (42uy, 108uy, 208uy), (111uy, 179uy, 255uy)

/// The hazard colour for an empty cell (brighter at the exposed pool surface —
/// where the cell above is not the same hazard), or None.
let private hazardColorAt (t: TerrainSnapshot) wx wy =
    // Bounds guard: the main view can be wider than the level, so callers pass
    // coordinates past the level edge (sky there, never a hazard).
    if wx < 0 || wx >= t.Width || wy < 0 || wy >= t.Height then
        None
    else
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

    // Extra shades derived from the theme palette: the shaded underside of
    // overhangs/ceilings, the near-sky tint for backdrop material, and the
    // lighter stonework of cosmetic pillars.
    let bodyDk = darken bodyA 16
    let skyHi = lighten skyC 15
    let pilHi = lighten bodyB 34
    let pilBand = lighten bodyB 26
    let pilBody = lighten bodyB 16

    // Terrain: only the visible window [camX, camX+viewW). Grass on the top
    // surface, dithered earth below (with a darker rim on exposed undersides so
    // ceilings and overhangs read); empty cells show sky — with faint backdrop
    // material — or a hazard pool where one has been carved into the ground
    // (so the terrain occludes its edges).
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
                    elif vy = h - 1 || not terrain.Solid.[(vy + 1) * tw + wx] then
                        bodyDk // shaded underside of a ceiling or overhang
                    else
                        // A pillar region dresses the block as a fluted
                        // roman/egyptian column: banded capital and base,
                        // vertical flutes between, darkened rims for roundness.
                        // Purely a paint job on the solid mask, so a bashed
                        // tunnel carves straight through the fluting.
                        let pillar =
                            level.Pillars
                            |> List.tryFind (fun p -> wx >= p.X && wx < p.X + p.W && vy >= p.Y && vy < p.Y + p.H)

                        match pillar with
                        | Some p ->
                            if vy <= p.Y + 2 || vy >= p.Y + p.H - 3 then pilBand // capital / base
                            elif wx = p.X || wx = p.X + p.W - 1 then bodyA // rounded rim
                            else
                                match (wx - p.X) % 3 with
                                | 1 -> pilHi // lit ridge of a flute
                                | 2 -> pilBody
                                | _ -> bodyA // flute groove
                        | None ->
                            if level.Theme = Brick && vy % 4 = 0 then bodyA // mortar line
                            elif (wx + vy) % 2 = 0 then bodyA
                            else bodyB
                else
                    match hazardColorAt terrain wx vy with
                    | Some c -> c
                    | None -> if skyDetail level.Theme h wx vy then skyHi else skyC

            paint data ((vy * viewW + vx) * 4) color

    ctx.putImageData (img, 0., 0.)

    // Theme scenery: only small, quiet ground props (tufts, mushrooms, torches),
    // drawn semi-transparent so they read as backdrop and never compete with the
    // lemmings. A prop vanishes once the ground it stands on is dug away, and is
    // drawn behind everything that matters — lemmings, hatch and exit paint over it.
    ctx?globalAlpha <- 0.55

    for d in level.Decor do
        if d.X + 16 >= camX && d.X - 16 < camX + viewW && terrain.IsSolid(d.X, d.Y) then
            // Tiny pixel-art: rects relative to the prop's base cell (dy up = negative).
            let r (color: string) dx dy w h =
                ctx?fillStyle <- color
                ctx.fillRect (float (d.X - camX + dx), float (d.Y + dy), float w, float h)

            match level.Theme, d.V % 2 with
            | Earth, 0 -> // grass tuft
                r "#3f9e42" -2 -3 1 3
                r "#2e7d32" 0 -4 1 4
                r "#3f9e42" 2 -3 1 3
            | Earth, _ -> // mushroom
                r "#d8cfc0" -1 -4 2 4
                r "#c23b2c" -3 -6 6 2
                r "#f0e8dc" 0 -6 1 1
            | Stone, _ -> // dry tuft
                r "#a89858" -2 -3 1 3
                r "#948448" 0 -4 1 4
                r "#a89858" 2 -3 1 3
            | Brick, _ -> // weeds in the paving cracks
                r "#5a7a4a" -2 -3 1 3
                r "#4a6a3e" 0 -4 1 4
                r "#5a7a4a" 2 -3 1 3
            | Hell, _ -> // torch with a flickering flame
                r "#2c1a14" -1 -6 2 6
                let f = (clock / 4 + d.X) % 3
                r "#ff8a1e" -1 (-9 - f) 2 (3 + f)
                r "#ffd23c" -1 (-8 - f) 1 2

    ctx?globalAlpha <- 1.0

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
