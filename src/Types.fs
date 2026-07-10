module FsLemming.Types

/// A skill assigned to a lemming.
/// A small, all-nullary DU: [<Struct>] makes it a value type (no heap alloc).
[<Struct>]
type Skill =
    | Walker
    | Digger
    | Blocker
    | Builder // lays a brick staircase up-and-forward
    | Basher // digs a horizontal tunnel
    | Miner // digs a diagonal tunnel down-and-forward
    | Climber // scales vertical walls instead of turning
    | Floater // drifts down slowly and survives any fall
    | Bomber // arms a fuse, then explodes

/// Visual theme for a level (terrain palette + background tint).
[<Struct>]
type Theme =
    | Earth
    | Stone
    | Brick
    | Hell

/// A deadly region: lava/fire kills instantly; water lets a lemming sink, then drown.
[<Struct>]
type HazardKind =
    | Lava
    | Water

/// A rectangular region in terrain pixels (hatch or exit).
[<Struct>]
type Region = { X: int; Y: int; W: int; H: int }

/// A purely decorative scenery prop standing on the terrain surface: (X, Y) is
/// the ground row it stands on, V picks one of a few shapes. It never collides —
/// presentation only. The THEME decides what a variant looks like (Earth: grass
/// tuft/mushroom, Stone/Brick: tufts, Hell: torch), so re-theming a level
/// re-skins its scenery for free.
[<Struct>]
type Decor = { X: int; Y: int; V: int }

/// Immutable, read-only view of the terrain handed to every lemming each tick.
///
/// This is the heart of the "no state leaking" idea: a lemming receives a COPY
/// of the world. It can read it to make decisions, but it physically cannot
/// reach back and mutate the real terrain — there is nothing shared to mutate,
/// hence no locks are ever needed.
type TerrainSnapshot =
    { Width: int
      Height: int
      Solid: bool[] // row-major; true = solid ground (dirt OR steel)
      Steel: bool[] // indestructible cells — diggers/bashers/miners can't clear these
      Lava: bool[] // deadly: instant death
      Water: bool[] // deadly: sink, then drown
      Blockers: Set<int * int> } // positions occupied by Blocker lemmings

    member t.IsSolid(x, y) =
        if x < 0 || x >= t.Width || y < 0 || y >= t.Height then false
        else t.Solid.[y * t.Width + x]

    /// True if this cell is indestructible steel (a digger/basher/miner stops here).
    member t.IsSteel(x, y) =
        x >= 0 && x < t.Width && y >= 0 && y < t.Height && t.Steel.[y * t.Width + x]

    /// True if a stationary Blocker blocks this cell. Walkers turn around here.
    /// This is how lemmings "see" each other — only as impassable cells in the
    /// shared snapshot, never as direct references to other actors.
    /// A blocker blocks a small vertical field (its cell ± a few px), not just
    /// its exact pixel — otherwise a walker on a bump or slope (auto-step-up
    /// reaches 3px) would be 1px off and slip straight past it.
    member t.IsBlocked(x, y) =
        not t.Blockers.IsEmpty
        && [ -4 .. 4 ] |> List.exists (fun dy -> t.Blockers.Contains(x, y + dy))

    /// The hazard (if any) at this cell.
    member t.HazardAt(x, y) =
        if x < 0 || x >= t.Width || y < 0 || y >= t.Height then
            None
        else
            let i = y * t.Width + x
            if t.Lava.[i] then Some Lava
            elif t.Water.[i] then Some Water
            else None

/// The only way to change the world: ask the World actor to apply a mutation.
type Mutation =
    | RemoveCircle of x: int * y: int * radius: int // diggers / bombers
    | RemoveRect of x: int * y: int * w: int * h: int // bashers / miners
    | AddTerrain of x: int * y: int * w: int * h: int // builders

/// A particle effect to spawn where a lemming died (presentation only).
[<Struct>]
type EffectKind =
    | Splat // fatal fall
    | Explode // bomb went off
    | Burn // walked into lava
    | Drown // sank in water

/// A flat, serialisable snapshot of a lemming, produced each tick purely for
/// rendering. The renderer never sees a lemming's private state — only this.
[<Struct>]
type LemmingView =
    { Id: int
      X: int
      Y: int
      Dir: int // facing: -1 left, +1 right (for sprite flipping)
      Skill: Skill
      FallDist: int // pixels fallen so far (so the renderer ignores tiny steps)
      Fuse: int // remaining bomb fuse ticks (0 = unarmed)
      Alive: bool }

/// What a lemming returns when ticked: its new public appearance, terrain edits,
/// and (when it died this tick) which death effect to show.
type TickResult =
    { View: LemmingView
      Mutations: Mutation list
      Effect: EffectKind option }

/// A level is pure data. Difficulty grows by changing these numbers, not the
/// engine — see docs/design.md, decision 3.
type Level =
    { Name: string
      Theme: Theme
      Width: int
      Height: int
      Terrain: bool[] // solid cells (dirt AND steel)
      Steel: bool[] // which solid cells are indestructible
      Lava: bool[] // deadly lava cells
      Water: bool[] // deadly water cells
      Hatch: Region // where lemmings drop in
      Exit: Region // reach here to be saved
      Decor: Decor list // theme-skinned scenery; never affects gameplay
      Pillars: Region list // cosmetic: solid blocks drawn as fluted columns
      SpawnCount: int // how many lemmings the hatch releases
      SpawnEveryTicks: int // one lemming every N ticks
      SaveTarget: int // how many must reach the exit to win
      TimeLimitTicks: int
      Inventory: Map<Skill, int> } // finite skills available this level

[<Struct>]
type Outcome =
    | Playing
    | Won
    | Lost

/// A read-only snapshot of the run, for the HUD.
[<Struct>]
type GameStatus =
    { Spawned: int
      Saved: int
      Dead: int
      Active: int
      Elapsed: int
      OpenProgress: float // 0..1 hatch-opening animation before spawning starts
      Outcome: Outcome }
