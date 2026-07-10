// Level-solvability checker. Replicates src/Lemming.fs step() + the World's
// terrain mutations (steel-aware) and runs ONE lemming through a scripted plan of
// skill assignments, reporting whether it reaches the exit. If a single lemming
// can solve the route, the crowd can too (with blockers/timing).
//
// It verifies the ACTUAL shipped data: it decodes src/levels.json (the output of
// gen_levels.fsx, surface relief included) rather than rebuilding terrain from a
// second copy of the rect lists. Regenerate levels, then re-run this.
//
// Run with:  dotnet fsi tools/solver.fsx

open System.IO
open System.Text.Json

let H = 150
type Lvl = { W: int; solid: bool[]; steel: bool[]; lava: bool[]; water: bool[] }

/// Expand RLE runs (alternating empty/solid, starting empty) into a flat grid.
let rleDecode (runs: int seq) size =
    let cells: bool[] = Array.zeroCreate size
    let mutable i = 0
    let mutable v = false
    for run in runs do
        for _ in 1..run do
            if i < size then cells.[i] <- v
            i <- i + 1
        v <- not v
    cells

type S = { X:int; Y:int; Dir:int; Skill:string; Counter:int; FallDist:int; Submerged:int; Alive:bool }
type Mut = RR of int*int*int*int | AT of int*int*int*int

/// (startX,startY,startDir) (exitX,exitY,exitW,exitH) plan=(triggerX,skill) list.
/// A plan item fires when the WALKER reaches triggerX in its travel direction —
/// modelling the player clicking once the lemming is in position.
let run (lv:Lvl) (sx,sy,sd) (ex,ey,ew,eh) (plan:(int*string) list) maxT =
    let w = lv.W
    let inb x y = x>=0 && x<w && y>=0 && y<H
    let isSolid x y = inb x y && lv.solid.[y*w+x]
    let isSteel x y = inb x y && lv.steel.[y*w+x]
    let haz x y = if not (inb x y) then 0 elif lv.lava.[y*w+x] then 1 elif lv.water.[y*w+x] then 2 else 0
    let apply = function
        | RR(rx,ry,rw,rh) -> for y in ry..ry+rh-1 do for x in rx..rx+rw-1 do if inb x y && not lv.steel.[y*w+x] then lv.solid.[y*w+x]<-false
        | AT(rx,ry,rw,rh) -> for y in ry..ry+rh-1 do for x in rx..rx+rw-1 do if inb x y then lv.solid.[y*w+x]<-true
    let step (s:S) : S * Mut list =
        if not s.Alive then s,[]
        elif s.Y>=H then {s with Alive=false},[]
        elif haz s.X s.Y = 1 then {s with Alive=false},[]
        elif haz s.X s.Y = 2 then (if s.Submerged>=14 then {s with Alive=false},[] else {s with Submerged=s.Submerged+1; Y=s.Y+(s.Submerged%2)},[])
        else
            let s = if s.Submerged>0 then {s with Submerged=0} else s
            if s.Skill="Climber" && isSolid (s.X+s.Dir) s.Y then
                if isSolid (s.X+s.Dir) (s.Y-1) then {s with Y=s.Y-1; FallDist=0},[] else {s with X=s.X+s.Dir; Y=s.Y-1; FallDist=0},[]
            elif s.Skill="Digger" then
                if isSteel s.X (s.Y+1) then {s with Skill="Walker"},[]
                elif [1..3]|>List.exists(fun k->isSolid s.X (s.Y+k)) then {s with Y=s.Y+1; FallDist=0},[RR(s.X-3,s.Y+1,7,1)]
                else {s with Skill="Walker"},[]
            elif s.Skill="Miner" then
                let d=s.Dir
                if isSteel (s.X+d) (s.Y+1) then {s with Skill="Walker"},[]
                elif [1..3]|>List.exists(fun k->isSolid (s.X+d*k) (s.Y+k)) then
                    let ax=if d>0 then s.X else s.X-7
                    {s with X=s.X+d; Y=s.Y+1; FallDist=0},[RR(ax,s.Y-10,8,12)]
                else {s with Skill="Walker"},[]
            elif not (isSolid s.X (s.Y+1)) then
                let nf=s.FallDist+1
                if s.Skill="Floater" && s.FallDist>3 && s.FallDist%2=1 then {s with FallDist=nf},[]
                else {s with Y=s.Y+1; FallDist=nf},[]
            elif s.FallDist>28 && s.Skill<>"Floater" then {s with Alive=false},[]
            else
                let s={s with FallDist=0}
                match s.Skill with
                | "Blocker" | "Digger" | "Miner" -> s,[]
                | "Builder" ->
                    if s.Counter>0 then
                        let bx=if s.Dir>0 then s.X+1 else s.X-4
                        {s with X=s.X+s.Dir; Y=s.Y-1; Counter=s.Counter-1},[AT(bx,s.Y,4,1)]
                    else {s with Skill="Walker"},[]
                | "Basher" ->
                    let d=s.Dir
                    let reach=[1..20]|>List.exists(fun k->isSolid (s.X+d*k) s.Y || isSolid (s.X+d*k) (s.Y-5))
                    let steelAhead=isSteel (s.X+d) s.Y || isSteel (s.X+d) (s.Y-5)
                    if steelAhead || not reach then {s with Skill="Walker"},[]
                    else let ax=if d>0 then s.X+1 else s.X-7 in {s with X=s.X+d},[RR(ax,s.Y-10,8,11)]
                | _ ->
                    let nx=s.X+s.Dir
                    if nx<0||nx>=w then {s with Dir = -s.Dir},[]
                    elif not (isSolid nx s.Y) then {s with X=nx},[]
                    else
                        let rec rise u = if u>3 then None elif not (isSolid nx (s.Y-u)) then Some u else rise (u+1)
                        match rise 1 with Some u -> {s with X=nx; Y=s.Y-u},[] | None -> {s with Dir = -s.Dir},[]
    let mutable s = { X=sx; Y=sy; Dir=sd; Skill="Walker"; Counter=0; FallDist=0; Submerged=0; Alive=true }
    let mutable pending = plan
    let inExit (s:S) = s.Alive && s.X>=ex && s.X<ex+ew && s.Y>=ey && s.Y<ey+eh
    let mutable t, res = 0, "TIMEOUT"
    while t<maxT && res="TIMEOUT" do
        (match pending with
         | (tx,sk)::rest when s.Skill="Walker" && ((s.Dir>0 && s.X>=tx) || (s.Dir<0 && s.X<=tx)) ->
             s <- { s with Skill=sk; Counter=(if sk="Builder" then 12 else 0) }; pending <- rest
         | _ -> ())
        let s2,muts = step s
        for m in muts do apply m
        s <- s2
        if not s.Alive then res <- sprintf "DIED (%d,%d)" s.X s.Y
        elif inExit s then res <- "SOLVED"
        t<-t+1
    res

// ---- Load the shipped campaign ------------------------------------------------
let jsonPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "src", "levels.json")
let doc = JsonDocument.Parse(File.ReadAllText jsonPath)

let runsOf (e: JsonElement) (field: string) =
    e.GetProperty(field).EnumerateArray() |> Seq.map (fun r -> r.GetInt32())

let rectOf (e: JsonElement) (field: string) =
    let r = e.GetProperty field
    r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(), r.GetProperty("w").GetInt32(), r.GetProperty("h").GetInt32()

// One scripted route per campaign level, in order (triggerX, skill).
let plans =
    [ [ (90,"Basher"); (160,"Floater") ]        // 1 bash the ledge wall, float off the cliff
      [ (100,"Basher"); (206,"Builder") ]       // 2 bash the wall, build onto the exit ledge
      [ (100,"Digger"); (190,"Basher") ]        // 3 dig through the slab, bash to the exit
      [ (60,"Miner"); (190,"Builder") ]         // 4 mine off the plateau, build onto the ledge
      [ (99,"Builder"); (170,"Climber") ]       // 5 bridge the lava, climb the wall
      [ (123,"Builder"); (180,"Basher") ]       // 6 bridge the gap, bash the wall
      [ (80,"Basher"); (165,"Climber") ]        // 7 bash the wall, climb the tall one
      [ (60,"Miner"); (180,"Basher") ]          // 8 mine off the plateau, bash the wall
      [ (90,"Miner"); (310,"Basher"); (429,"Builder") ] // 9 mine, bash, bridge the lava
      [ (119,"Builder"); (209,"Builder") ] ]    // 10 bridge the lava AND the water

let levels = doc.RootElement.EnumerateArray() |> Seq.toList

if List.length levels <> List.length plans then
    failwithf "levels.json has %d levels but solver has %d plans" (List.length levels) (List.length plans)

printfn "Campaign solvability (from src/levels.json):"

for e, plan in List.zip levels plans do
    let w = e.GetProperty("width").GetInt32()
    let size = w * H
    let lv =
        { W = w
          solid = rleDecode (runsOf e "terrain") size
          steel = rleDecode (runsOf e "steel") size
          lava = rleDecode (runsOf e "lava") size
          water = rleDecode (runsOf e "water") size }
    // Spawn exactly like Game.fs does: hatch centre, hatch top (then fall).
    let hx, hy, hw, _ = rectOf e "hatch"
    let name = e.GetProperty("name").GetString()
    printfn "  %-24s %s" name (run lv (hx + hw / 2, hy, 1) (rectOf e "exit") plan 9000)
