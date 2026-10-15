# FsLemming

[>> Play online](https://thorium.github.io/FsLemming/)

**📱 Install as an app:** open the same link on your phone or tablet and tap **Install app** in the banner (Android / Chrome), or **Share → Add to Home Screen** (iPhone / iPad). The game then launches full-screen from your home screen and works offline.

A small F# tutorial on **message-passing architecture**: encapsulated state, no
shared mutable data, no manual locks. The demo is a slice of *Lemmings* — many
tiny independent characters plus one shared world — compiled to JavaScript with
[Fable 5](https://fable.io) and runnable on GitHub Pages.

It's built on F#'s `MailboxProcessor` (what F# calls an **agent**). That's
*actor-style* and teaches the actor lessons — but it isn't a full actor model;
see [docs/design.md](docs/design.md#a-note-on-terminology) for the precise
differences (control flow, request/reply, supervision, distribution). We say
"agent" loosely for the share-nothing, one-message-at-a-time idea — nothing to do
with "AI agents".

## The idea

| Concern | Who owns it | How others interact |
|---|---|---|
| A lemming's position/skill | each `Lemming` agent (private) | send `Tick` / `SetSkill` messages |
| The destructible terrain | the single `World` agent (private) | ask for a `Snapshot`, send `Apply` edits |
| The frame loop | `Game` coordinator | runs a deterministic two-phase tick |
| Drawing | `Render` (dumb) | only ever sees immutable snapshots |

Each agent is an F# `MailboxProcessor`. Its state lives inside the mailbox
closure and **never leaks** — the only outputs are immutable reply messages.
On Fable's single-threaded JS runtime you never need a lock; the same code is
also race-free if compiled to a truly parallel target (e.g. Fable's BEAM
backend).

### The two-phase tick (why it's deterministic)

1. **Sense/decide** — every lemming reacts to the *same* start-of-tick terrain
   snapshot and returns its intended move + any terrain edits. They mutate only
   their own private state, so run order can't change the result.
2. **Commit** — all terrain edits are applied together by the `World` actor.

## Run it

Prerequisites: the **.NET SDK 10.x** and **Node.js**. Then two commands:

```bash
npm install   # installs Vite AND restores the Fable compiler (via postinstall)
npm start     # compiles F# -> JS and serves at http://localhost:5173
```

`npm run dev` is an alias of `npm start`. **Stop the dev server with `Ctrl+C`** —
Vite's `q`-to-quit shortcut isn't available here because Vite runs as a child of
`dotnet fable --run` and doesn't own the terminal's stdin.

Build the static site into `dist/`:

```bash
npm run build
```

> This is a **Fable (F# → JavaScript)** project, not a console app. `dotnet run`
> and `dotnet build` will *not* launch it — always use the `npm` scripts above.
> (`npm install` runs `dotnet tool restore` for you, so there's no separate
> tool-restore step.)

## Levels

Levels are **data**, not code. They live in `src/levels.json` (terrain run-length
encoded to stay tiny), bundled via Vite's JSON import and decoded at startup by
`src/LevelJson.fs`. The campaign is authored in a generator:

```bash
dotnet fsi tools/gen_levels.fsx   # rewrites src/levels.json
```

Edit the level list in `tools/gen_levels.fsx` and re-run to change the campaign —
the engine never changes. Besides the authored shapes, the generator adds gentle
surface relief (1–2px mounds — walkers auto-step 3px, so routes are untouched)
and scenery spots: tiny `{x, y, v}` entries whose look is decided by the level's
*theme* at render time (grass tufts and mushrooms on earth, torches in hell),
drawn faintly so they stay backdrop. Scenery never collides, and it disappears
with the ground it stands on. The generator also hangs organic ceiling masses
from the top edge — bumpy cave roofs with the odd stalactite drip, placed only
where no route can reach them — and the renderer adds a faint procedural sky
backdrop (roots, pillars, chains, stalactites) from a coordinate hash.
After regenerating, verify every puzzle still solves:

```bash
dotnet fsi tools/solver.fsx       # replays each level's scripted route from src/levels.json
```

The solver replays the route each level was designed around with the game's own
rules (`Lemming.step` and `World.applyMutation`, loaded from `src/`), so it can't
drift from the game.

### Checking levels

`src/LevelCheck.fs` checks whether a level is fair and solvable. It runs one
lemming with the game's own rules — the pure `Lemming.step` and
`World.applyMutation`, called directly rather than through the actors — and
searches for the plan with the fewest skills (up to 3, from the level's own
inventory) that saves it. It reports:

- a hatch inside terrain, over a bottomless drop, or over a fall the lemmings
  can't survive (more than 28 px) without enough Floaters;
- an exit outside the map, buried in terrain, or all lava/water;
- where lemmings with no skills end up — splat, burnt, drowned, off the map, or
  walking back and forth for ever — and a warning when they reach the exit
  without any skills (not a puzzle);
- the solution it finds ("Basher at (90, 89), then Floater at (120, 89)"), or
  that it found no plan of up to 3 skills (a problem: the level is probably
  impossible — the search gives skills only to walking or just-falling
  lemmings and merges routes that reach the same spot, so it's not a proof);
- a save target above the lemmings released, too little time for the last
  needed lemming, and too few Floaters/Climbers when every saved lemming needs one.

It is a one-lemming check: Blockers and Bombers (which only help a crowd) aren't
searched. Only the editor and the tools load it — the game never does.

The same check runs from the command line, for scripts and agents:

```bash
dotnet fsi tools/levels.fsx --check [levels.json] [--level N] [--json]   # findings (+ spots in JSON)
dotnet fsi tools/levels.fsx --solve [levels.json] [--level N]            # the plan it finds
dotnet fsi tools/levels.fsx --render [levels.json] out/ --route          # PNG: walk, solution, problems
dotnet fsi tools/levels.fsx --compare before.json after.json out/        # before | after | changes
```

The file defaults to `src/levels.json` (the editor's downloaded `levels.json`
works too); `--level` takes a number or part of a name. `--check` exits with
code 1 when it finds a problem. `tools/LevelFile.fsx` is the one `levels.json`
reader both tools share.

There's also an **in-browser level editor** at `editor.html` (linked from the
game): import an image (dark pixels → solid terrain), place the hatch/exit, set
the parameters, press **Check level** — it draws the no-skill walk (grey) and
the solution (yellow, a dot where each skill is given) on the map and lists what
it found (the search runs in a Web Worker, so the page stays responsive; an
edit stops it); **Exit onto ground** and **Hatch to safe height** fix the two simplest
problems — then either:

- **Save to browser** — stores the level in `localStorage`; the game appends any
  saved levels to its campaign automatically on load.
- **Download `levels.json`** — a file in the same format you can commit to
  `src/levels.json` or load at runtime.

In the **game**, a level dropdown lets you jump to any level (the URL tracks it
as `?level=N`, so levels are linkable), and **Load levels file** imports a
`levels.json` from disk and appends it to the playable set. All
of this is browser-local (file picker + `localStorage`) — there's no server.
The editor reuses the domain types and the `LevelJson` encoder, so the engine is
untouched.

## Deploy to GitHub Pages

`.github/workflows/deploy.yml` builds and publishes `dist/` on every push to
`main`. In the repo settings set **Pages → Source → GitHub Actions**.

## Files

| Layer | File | Knows about |
|---|---|---|
| **Domain** | `src/Types.fs` | types and messages — no graphics |
| | `src/World.fs` | the World actor (owns the terrain) |
| | `src/Lemming.fs` | the Lemming actor (+ pure, testable `step`) |
| | `src/Inventory.fs` | the Inventory actor (finite per-level skill counts) |
| | `src/Game.fs` | the coordinator / level runner (two-phase tick, spawn, exit, win/lose) |
| | `src/LevelJson.fs` | encodes/decodes `levels.json` (RLE terrain, skills) ↔ `Level`s |
| | `src/Storage.fs` | browser-local (`localStorage`) save/load of user levels |
| | `src/Levels.fs` | loads + decodes the built-in campaign from `levels.json` |
| **Presentation** | `src/Sprites.fs` | **the graphics seam** — sheet layout, frame indices, image loading |
| | `src/Render.fs` | blits the visible camera window + the minimap |
| | `src/App.fs` | the game page — DOM wiring, camera, game loop |
| | `src/Editor.fs` | the level-editor page (`editor.html`) |
| **Editor & tools** | `src/LevelCheck.fs` | the level check + solution search (runs `Lemming.step` / `World.applyMutation` directly; not loaded by the game) |
| | `src/CheckWorker.fs` | the editor's Web Worker: runs the check's search off the page thread |
| | `tools/levels.fsx` | level commands: check, solve, render, compare |
| | `tools/solver.fsx` | campaign regression: each level's scripted route |
| | `tools/LevelFile.fsx` | the `levels.json` reader the tools share |

The domain layer never references the presentation layer. `Render`/`Sprites`
only ever receive immutable `TerrainSnapshot` / `LemmingView` values, so you can
replace the art or the entire renderer without touching the simulation.

## Graphics

`public/lemmings.png` is an **original** sprite sheet (our own green-haired,
blue-bodied little guys — not ripped from the copyrighted original, so it's safe
to publish). It's generated by `tools/gen_sprites.fsx` from editable pixel maps:

```bash
dotnet fsi tools/gen_sprites.fsx   # rewrites public/lemmings.png
```

Sheet layout = 16x16 frames, one **column** per animation frame (walk ×4, then
faller / digger / basher / miner / blocker / builder / climber / floater ×2 each)
and one **row** per skin tone (see `FRAMES` / `SKINS` in the generator). Each
pose cycles through its own frames at the renderer's animation tick, so frame
counts can differ per pose. The renderer also picks a row per lemming by id, so
the crowd is a mix of tones. To swap in different art, replace the PNG and adjust
the per-pose column arrays / `variantCount` in `src/Sprites.fs` — that's the only
file that needs to change. See `docs/authentic-graphics.md` for using the real
game's `MAIN.DAT` sprites instead.
