module FsLemming.Sprites

open Browser
open Browser.Types
open FsLemming.Types

/// THE GRAPHICS SEAM.
///
/// This is the ONLY module that knows what a lemming looks like — the sprite
/// sheet layout, pixel sizes, and which frame to show for a given state. The
/// domain model (Types/World/Lemming/Game) never references any of this, so the
/// whole rendering layer (or the art) can be swapped without touching the
/// simulation. Want authentic ripped graphics instead of our originals? Replace
/// public/lemmings.png and the indices below; nothing else changes.

/// Pixel size of one (square) frame in the sheet.
let frameSize = 16

/// Number of skin-tone rows in the sheet. MUST match SKINS.Length in
/// tools/gen_sprites.fsx. The renderer spreads lemmings across these.
let variantCount = 4

// Sheet columns grouped per pose. MUST match the FRAMES order in
// tools/gen_sprites.fsx. Each pose can have any number of frames.
let private walks = [| 0; 1; 2; 3 |]
let private falls = [| 4; 5 |]
let private digs = [| 6; 7 |]
let private bashes = [| 8; 9 |]
let private mines = [| 10; 11 |]
let private blocks = [| 12; 13 |]
let private builds = [| 14; 15 |]
let private climbs = [| 16; 17 |]
let private floats = [| 18; 19 |]

/// The sheet image. Created and loaded once; `loaded` gates drawing until ready.
let image: HTMLImageElement =
    document.createElement "img" :?> HTMLImageElement

let mutable loaded = false

image.addEventListener ("load", fun _ -> loaded <- true)
image.src <- "lemmings.png" // served from public/ at site root

/// Map a lemming's current visual state to a sheet column. `tick` is a free-
/// running animation counter from the renderer's clock; each pose cycles through
/// its own frames at `tick % count`. `isFalling`/`isClimbing` are derived by the
/// renderer from the snapshot — all presentation, kept OUT of the domain model.
let frameColumn (skill: Skill) (isFalling: bool) (isClimbing: bool) (tick: int) =
    let pick (frames: int[]) = frames.[(abs tick) % frames.Length]

    if isClimbing then
        pick climbs
    elif isFalling then
        if skill = Floater then pick floats else pick falls // umbrella vs tumbling
    else
        match skill with
        | Walker
        | Floater
        | Bomber // walks while its fuse burns; the crater is the visible payoff
        | Climber -> pick walks
        | Digger -> pick digs
        | Blocker -> pick blocks
        | Builder -> pick builds
        | Basher -> pick bashes
        | Miner -> pick mines
