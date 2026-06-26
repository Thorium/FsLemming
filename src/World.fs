module FsLemming.World

open FsLemming.Types

/// The World actor owns the one piece of genuinely shared state — the terrain.
///
/// In a naive design every lemming would read and write a shared mutable grid,
/// and you'd reach for locks. Here, the grid lives *inside* the MailboxProcessor
/// closure. Nobody else has a reference to it. The only way to interact is by
/// sending a message, so all access is serialised through the mailbox for free.
type private Msg =
    | Snapshot of AsyncReplyChannel<TerrainSnapshot>
    | Apply of Mutation list
    | SetBlockers of Set<int * int>

type World(width: int, height: int, initialSolid: bool[], steel: bool[], lava: bool[], water: bool[]) =

    let agent =
        MailboxProcessor.Start(fun inbox ->
            // PRIVATE state. This array never escapes this function.
            let solid = Array.copy initialSolid
            // The current set of Blocker positions. Replaced wholesale each tick
            // by the coordinator, so dead/removed blockers drop out for free.
            let mutable blockers : Set<int * int> = Set.empty

            // Clearing terrain (value = false) never removes indestructible steel.
            let setRect rx ry rw rh value =
                for y in ry .. ry + rh - 1 do
                    for x in rx .. rx + rw - 1 do
                        if x >= 0 && x < width && y >= 0 && y < height then
                            let i = y * width + x
                            if value || not steel.[i] then solid.[i] <- value

            let removeCircle cx cy r =
                for y in cy - r .. cy + r do
                    for x in cx - r .. cx + r do
                        if x >= 0 && x < width && y >= 0 && y < height then
                            let dx, dy = x - cx, y - cy
                            let i = y * width + x
                            if dx * dx + dy * dy <= r * r && not steel.[i] then
                                solid.[i] <- false

            let snapshot () =
                { Width = width
                  Height = height
                  Solid = Array.copy solid
                  Steel = steel // static; shared by reference
                  Lava = lava
                  Water = water
                  Blockers = blockers }

            let rec loop () =
                async {
                    let! msg = inbox.Receive()

                    // A failed message must not kill the actor; on a Snapshot we
                    // still answer (best effort) so the coordinator can't hang.
                    try
                        match msg with
                        | Snapshot reply ->
                            // Immutable COPY: readers can never mutate us.
                            // (blockers is an immutable F# Set, so sharing it is safe.)
                            reply.Reply(snapshot ())
                        | Apply mutations ->
                            for m in mutations do
                                match m with
                                | RemoveCircle(cx, cy, r) -> removeCircle cx cy r
                                | RemoveRect(rx, ry, rw, rh) -> setRect rx ry rw rh false
                                | AddTerrain(rx, ry, rw, rh) -> setRect rx ry rw rh true
                        | SetBlockers bs -> blockers <- bs
                    with ex ->
                        eprintfn "World failed on a message: %O" ex

                        match msg with
                        | Snapshot reply -> reply.Reply(snapshot ())
                        | _ -> ()

                    return! loop ()
                }

            loop ())

    /// Phase 1 of a tick: read the world.
    member _.Snapshot() = agent.PostAndAsyncReply Snapshot

    /// Phase 2 of a tick: commit all terrain edits in one place.
    member _.Apply(mutations) = agent.Post(Apply mutations)

    /// Replace the set of Blocker positions (sent by the coordinator each tick).
    member _.SetBlockers(positions) = agent.Post(SetBlockers positions)
