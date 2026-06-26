# Design notes — skills, levels, and the agent boundaries

This is the plan for growing the demo from "walkers + diggers" into a real
Lemmings-style game **without** weakening the message-passing architecture. It
records *why* things are shaped the way they are; the engine is extended to
match this incrementally.

Guiding rule: the domain layer (`Types` / `World` / `Lemming` / `Game`) never
shares mutable state and never holds references between agents. Everything an
agent needs from the outside arrives as an immutable message; everything it
exposes leaves as an immutable snapshot. If a new feature seems to need shared
state, that's the signal to give the state its own agent — not a lock.

## A note on terminology

The thing this tutorial actually teaches is **message-passing with share-nothing,
encapsulated state** — no shared mutable data, no locks. We build it on F#'s
`MailboxProcessor`, which F# itself calls an **agent**: a concurrency primitive,
*nothing to do with "AI agents"*. It's *actor-style*, and the lessons
(encapsulation, one message at a time, no locks) are the actor lessons — but it
is **not** a full actor model/runtime. Being precise about the differences:

- **Control flow / selective receive.** A pure (Hewitt) actor reacts to one
  message and *designates the next behavior*; it doesn't pause mid-handler to
  await a particular message. A `MailboxProcessor` body is an ordinary `async`
  loop — it may `Receive()` anywhere, branch and loop, and `Scan` for matching
  messages while leaving others queued. More expressive than "message → next
  behavior" purity. (Erlang's selective `receive` is similar, so F# agents sit
  closer to Erlang processes than to the minimal theoretical model.)
- **Built-in request/reply.** Pure actors are fire-and-forget; replies are
  emulated via a return address. F# bakes it in with `AsyncReplyChannel`
  (`PostAndReply` / `PostAndAsyncReply`). Our `Snapshot`, `TryTake` and `Tick`
  are really *calls* (await a reply), not one-way messages.
- **No supervision / fault model.** No "let it crash" + restart supervision trees
  (Erlang/OTP, Akka). An agent just stops on an unhandled exception — which is
  why each loop has a manual `try/with`.
- **In-process only.** No addresses, registry, or remoting (no location
  transparency / distribution); agents are plain object references.
- **Lifecycle.** We *do* show graceful shutdown: a lemming agent handles a `Stop`
  message by ending its loop (so the agent terminates), and the coordinator sends
  it when a lemming is retired (reached the exit / died) or the level is left.
  This is the actor "terminate" concept; supervision/restart we deliberately skip.

So "message-passing agents" is the precise framing. Elsewhere in these docs and
in code comments we still use "actor" loosely for the share-nothing,
one-message-at-a-time idea — not to claim a full actor runtime.

## Skills

Canonical Lemmings names (so we share vocabulary), and how each maps onto the
model. The key distinction is **pure-local** skills (change only the lemming's
own private state) vs **world-mutating** skills (ask the `World` actor to change
the terrain). Neither needs a lock.

| Skill | Effect | Mechanism | Touches |
|---|---|---|---|
| **Walker** | default: walk, turn at walls | already implemented | local |
| **Climber** | climbs vertical walls instead of turning | private flag in lemming state | local |
| **Floater** | survives / slows long falls | private flag; changes fall handling | local |
| **Digger** | digs straight down | `RemoveCircle` below (implemented) | terrain |
| **Basher** | digs horizontally ahead | terrain removal ahead while advancing | terrain |
| **Miner** | digs diagonally down-forward | terrain removal down+forward | terrain |
| **Builder** | lays a brick staircase (~12 steps) | **`AddTerrain`** steps over N ticks | terrain |
| **Blocker** | stops; makes others turn around | registers an obstacle via the `World` | world-mediated |
| **Bomber** | countdown, then explodes and dies | private countdown → `RemoveCircle` + die | local + terrain |
| **Nuke** | "explode all" — every lemming bombs | broadcast staggered Bomber to all | broadcast |

Note `Climber`/`Floater` are entirely self-contained — a clean illustration that
"capability" is just private state, no coordination required. `Bomber` is the
nicest teaching case: a fully encapsulated countdown that, on expiry, emits one
terrain mutation and removes itself.

### Engine changes implied

1. **Grow `Skill`** (`Types.fs`) from `Walker | Digger` to the full set above.
   `Lemming.step` gains a branch per skill; each branch stays a pure function of
   `(terrain, state) -> state * Mutation list`, so all behaviour remains unit-
   testable without any actors.
2. **Generalize `Mutation`** (`Types.fs`) — `RemoveCircle` (diggers/bombers) was
   joined by `RemoveRect` (bashers/miners) and `AddTerrain` (builders). `World`
   gets one match arm per case; nothing else changes because all terrain edits
   already funnel through `World.Apply`. See the `Mutation` type in `src/Types.fs`.

## Decision 1 — skill inventory is its own actor

A level grants finite skills ("10 Diggers, 5 Builders, 2 Bombers…"). That count
is shared mutable state, so it gets a mailbox of its own rather than a field
someone mutates. The counts live inside the actor's closure; the only way to
spend one is the atomic `TryTake` message (check-and-decrement in one hop). See
`src/Inventory.fs`.

Flow when the player clicks a lemming with skill `s` selected:

1. UI → `Inventory.TryTake s`
2. if `true`, coordinator → `lemming.SetSkill s`
3. if `false`, ignore (no skill left)

The decrement and the assignment can't race because each is a single message to
a single mailbox. This is the canonical "don't share a counter" lesson in
miniature.

## Decision 2 — Blocker goes *through* the World

A Blocker is the second piece of shared spatial state after the terrain. If
lemmings asked each other "is anyone blocking ahead?", they'd need references to
one another and the clean boundary would collapse.

Instead, a Blocker tells the `World` "I occupy column X", and the
`TerrainSnapshot` handed out each tick reports those columns as impassable (e.g.
a separate `Blockers: Set<int*int>` on the snapshot, or folded into solidity).
Walkers keep reading *only* the snapshot — they never learn other lemmings
exist. When the Blocker is removed (bombed, or level reset), it tells the World
to clear its column.

This keeps the invariant: **a lemming's only window into the shared world is the
terrain snapshot.**

## Decision 3 — levels are data, not code

Difficulty grows by changing numbers, not the engine. A level is a pure-data
record (`Level` in `src/Types.fs`): name, dimensions, the `Terrain` bitmap, a
`Hatch` and `Exit` `Region`, spawn count + cadence (`SpawnEveryTicks`), the
`SaveTarget`, a time limit, and the per-level `Inventory: Map<Skill,int>`.
(`Terrain` could equally be loaded from a source such as `LEVEL000.DAT`.)

The campaign is just `Level list` (`src/Levels.fs`), ordered easy→hard. The
progression you described — first level needs one skill, later ones need several
in combination — is expressed purely by shrinking `Inventory` and tightening
`SaveTarget` / `TimeLimitTicks`. The simulation code stops growing while the game
keeps expanding.

A small **Spawner** (hatch) and **Exit** complete the loop, both owned by the
`Game` coordinator: it spawns a new `Lemming` from the hatch every
`SpawnEveryTicks` until `SpawnCount` is reached; each tick it retires lemmings
whose view falls inside the `Exit` (saved) or who died; you win when
`saved >= SaveTarget`, lose on timeout or when everyone's been released and none
remain short of target.

## Suggested build order

1. ~~Generalize `Mutation` (+ `AddTerrain`), keep current behaviour green.~~ ✅
2. ~~Add `Blocker` via the World (introduces the snapshot-of-obstacles idea).~~ ✅
3. ~~Add `Builder` (first user of `AddTerrain`) and `Basher`/`Miner`.~~ ✅
4. ~~Add `Floater`/`Climber` (pure-local; trivial once `step` branches exist).~~ ✅
   (Also added fall-damage / splat — the hazard that makes Floater worth having.)
5. ~~Add `Bomber`, then `Nuke` (broadcast).~~ ✅
6. ~~Add the `Inventory` actor + skill-selection UI.~~ ✅
7. ~~Add `Spawner`/`Exit` + the `Level` record; wire a 2–3 level campaign.~~ ✅

Each step is independently runnable and keeps the domain lock-free.

**All seven steps complete.** The build order is done: the engine is feature-
complete for a small Lemmings-style game, and the domain stayed lock-free
throughout.
