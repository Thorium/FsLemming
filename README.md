# FsLemming

[>> Play online](https://thorium.github.io/FsLemming/)

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
dotnet fsi tools/solver.fsx       # replays each level from src/levels.json
```

There's also an **in-browser level editor** at `editor.html` (linked from the
game): import an image (dark pixels → solid terrain), place the hatch/exit, set
the parameters, then either:

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
