module FsLemming.Storage

// Browser-local persistence for user-created levels, via localStorage. Levels are
// stored as the same levels.json text the rest of the app uses. The editor and
// the game are separate pages on the same origin, so they share this storage.

open Browser
open FsLemming.Types

let private key = "fslemming.userLevels"

/// User levels saved in this browser (empty if none / unreadable).
let loadUserLevels () : Level list =
    let json = window.localStorage.getItem key
    if isNull json || json = "" then
        []
    else
        try
            LevelJson.decodeCampaignJson json
        with _ ->
            []

let saveUserLevels (levels: Level list) =
    window.localStorage.setItem (key, LevelJson.encodeCampaign levels)

/// Append one level to the browser's saved set.
let addUserLevel (lvl: Level) =
    saveUserLevels (loadUserLevels () @ [ lvl ])

let count () = List.length (loadUserLevels ())
