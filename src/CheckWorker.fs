module FsLemming.CheckWorker

// The level editor's solution search, run in a Web Worker so the page stays
// responsive (an unsolvable level spends the whole search budget, several
// seconds). The editor posts one level as levels.json text; the worker posts
// back { found, plan, complete } — the plan as LevelCheck.planToInts. Only the
// editor starts this file; the game never loads it.

open Fable.Core
open Fable.Core.JsInterop

[<Emit("self.onmessage = $0")>]
let private onMessage (handler: obj -> unit) : unit = jsNative

[<Emit("self.postMessage($0)")>]
let private post (message: obj) : unit = jsNative

onMessage (fun e ->
    match LevelJson.decodeCampaignJson (e?data: string) with
    | [ level ] ->
        let plan, complete = LevelCheck.solve level
        post (
            createObj
                [ "found" ==> plan.IsSome
                  "plan" ==> (plan |> Option.map LevelCheck.planToInts |> Option.defaultValue [||])
                  "complete" ==> complete ]
        )
    | _ -> post null)
