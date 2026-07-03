module FsLemming.Inventory

open FsLemming.Types

/// The per-level skill allowance ("10 Diggers, 5 Builders, …").
///
/// This count is shared mutable state: many clicks could race to spend the last
/// Digger. Rather than a lock around a dictionary, we give the counts their own
/// mailbox. `TryTake` is a single atomic message — check-and-decrement happen
/// inside the actor, so two near-simultaneous clicks can never both win the last
/// one. This is decision 1 in docs/design.md, in miniature.
type private Msg =
    | TryTake of Skill * AsyncReplyChannel<bool>
    | Refund of Skill // give one back (an assignment raced with the lemming retiring)
    | Remaining of AsyncReplyChannel<Map<Skill, int>>

type Inventory(initial: Map<Skill, int>) =

    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop (counts: Map<Skill, int>) =
                async {
                    let! msg = inbox.Receive()

                    // As in Lemming: compute next state in try/with (always answer
                    // the reply so callers can't hang), recurse outside the try.
                    let next =
                        try
                            match msg with
                            | TryTake(skill, reply) ->
                                match Map.tryFind skill counts with
                                | Some n when n > 0 ->
                                    reply.Reply true // granted: one in stock
                                    Map.add skill (n - 1) counts
                                | _ ->
                                    reply.Reply false // none left (or never offered)
                                    counts
                            | Refund skill ->
                                let n = counts |> Map.tryFind skill |> Option.defaultValue 0
                                Map.add skill (n + 1) counts
                            | Remaining reply ->
                                reply.Reply counts
                                counts
                        with ex ->
                            eprintfn "Inventory failed on a message: %O" ex
                            (match msg with
                             | TryTake(_, reply) -> reply.Reply false
                             | Remaining reply -> reply.Reply counts
                             | Refund _ -> ())
                            counts

                    return! loop next
                }

            loop initial)

    /// Atomically spend one of `skill`. Returns true if one was available.
    member _.TryTake(skill) = agent.PostAndAsyncReply(fun rc -> TryTake(skill, rc))

    /// Give one `skill` back — used when a granted assignment couldn't be
    /// delivered (the lemming retired while the take was in flight).
    member _.Refund(skill) = agent.Post(Refund skill)

    /// Current counts, for the UI.
    member _.Remaining() = agent.PostAndAsyncReply Remaining
