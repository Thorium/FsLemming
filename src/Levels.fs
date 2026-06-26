module FsLemming.Levels

open Fable.Core.JsInterop
open FsLemming.Types

// Levels are data. They're authored in tools/gen_levels.fsx, emitted to
// levels.json, bundled by Vite (which parses JSON imports), and decoded here at
// startup. To change the campaign, edit the generator and re-run it — or, later,
// produce levels.json from the in-browser editor. The engine never changes.

let private data: obj = importDefault "./levels.json"

let campaign: Level list = LevelJson.decodeCampaign data
