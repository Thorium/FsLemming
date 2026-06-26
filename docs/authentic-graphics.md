# Using the original game's graphics (optional)

The tutorial ships with **original** pixel art (`public/lemmings.png`, generated
by `tools/gen_sprites.fsx`). If you'd rather use the *actual* classic sprites,
here's the clean way to do it — and it touches **only** the presentation layer
(`Sprites.fs` / `Render.fs`), never the domain model.

> ⚠️ Copyright: the original `MAIN.DAT` sprites are Psygnosis/Sony property. Fine
> for local/experimental use; do **not** commit them to a public repo or deploy
> them to GitHub Pages. The repo's own art exists precisely so the published
> demo is clean.

## Where the assets live

- DOS Lemmings data files (`MAIN.DAT`, `LEVEL000.DAT`, `GROUND*.DAT`,
  `VGAGR*.DAT`) — e.g. from the Internet Archive copy of the game.
- Format documentation — The Lemmings Archive:
  - `MAIN.DAT` (the lemming animation frames + terrain masks):
    <https://www.camanis.net/lemmings/files/docs/lemmings_main_dat_file_format.txt>
  - `.dat` compression + `.LVL` level layout:
    <https://www.camanis.net/lemmings/files/docs/lemmings_dat_file_format.txt>
  - Tools index: <https://www.camanis.net/lemmings/tools.php>

## Recommended approach

1. Use an existing extractor (e.g. the Lemmini / SuperLemmini Java tools, which
   already decode `MAIN.DAT`) to export the walker/digger/faller animations as
   PNGs.
2. Pack them into a single sprite sheet with the **same layout** this project
   expects: 16x16 (or your chosen `frameSize`) frames, one column per pose.
3. Drop it in as `public/lemmings.png` and update the constants in
   `src/Sprites.fs` (`frameSize`, the column indices, `variantCount`).

Because the simulation only ever emits `LemmingView` / `TerrainSnapshot`, none
of the domain code changes — the same `step` logic drives whatever art you blit.

## Real level data (optional)

`LEVEL000.DAT` decompresses to a 2048-byte struct describing terrain/object
placement. To load level 1 instead of the hand-built terrain in `App.fs`, parse
it into the `bool[]` solidity grid passed to `World(...)`. Again: domain
untouched — only the terrain *source* changes.
