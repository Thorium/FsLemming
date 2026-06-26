// Level-solvability checker. Replicates src/Lemming.fs step() + the World's
// terrain mutations (steel-aware) and runs ONE lemming through a scripted plan of
// skill assignments, reporting whether it reaches the exit. If a single lemming
// can solve the route, the crowd can too (with blockers/timing). Used to verify
// every campaign puzzle in tools/gen_levels.fsx is actually solvable.
//
// Run with:  dotnet fsi tools/solver.fsx

let H = 150
type Lvl = { W: int; solid: bool[]; steel: bool[]; lava: bool[]; water: bool[] }

/// Build masks the same way gen_levels does: rects = dirt; hazards carve lava/water
/// pits and add solid+indestructible steel.
let build w (rects: (int*int*int*int) list) (haz: (string*int*int*int*int) list) =
    let size = w*H
    let solid, steel, lava, water = Array.zeroCreate size, Array.zeroCreate size, Array.zeroCreate size, Array.zeroCreate size
    let fill (m: bool[]) x0 y0 ww hh v =
        for y in y0..y0+hh-1 do
            for x in x0..x0+ww-1 do
                if x>=0 && x<w && y>=0 && y<H then m.[y*w+x] <- v
    for (x0,y0,ww,hh) in rects do fill solid x0 y0 ww hh true
    for (k,x,y,ww,hh) in haz do
        match k with
        | "steel" -> fill steel x y ww hh true; fill solid x y ww hh true
        | "lava" -> fill lava x y ww hh true; fill solid x y ww hh false
        | "water" -> fill water x y ww hh true; fill solid x y ww hh false
        | _ -> failwithf "bad hazard %s" k
    { W=w; solid=solid; steel=steel; lava=lava; water=water }

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

let private groundOf w = (0,126,w,24)
let check name lv start exit plan = printfn "  %-22s %s" name (run lv start exit plan 9000)

printfn "Campaign solvability:"
check "1 bash+float"  (build 280 [ groundOf 280; (0,90,180,8); (110,68,18,30) ] []) (16,89,1)  (220,116,16,10) [ (90,"Basher"); (160,"Floater") ]
check "2 bash+build"  (build 280 [ groundOf 280; (110,96,18,30); (220,114,60,12) ] []) (16,125,1) (244,104,16,12) [ (100,"Basher"); (206,"Builder") ]
check "3 dig+bash"    (build 260 [ groundOf 260; (0,110,200,8); (200,60,18,89) ] []) (16,109,1) (230,116,16,10) [ (100,"Digger"); (190,"Basher") ]
check "4 mine+build"  (build 260 [ groundOf 260; (0,60,120,90); (200,114,40,12) ] []) (16,59,1)  (220,104,16,12) [ (60,"Miner"); (190,"Builder") ]
check "5 build+climb" (build 280 [ groundOf 280; (180,40,16,86) ] [ ("lava",100,126,12,24) ]) (16,125,1) (180,30,16,10) [ (99,"Builder"); (170,"Climber") ]
check "6 bridge+bash" (build 280 [ (0,126,124,24); (136,126,144,24); (190,96,18,30) ] []) (16,125,1) (250,116,16,10) [ (123,"Builder"); (180,"Basher") ]
check "7 bash+climb"  (build 280 [ groundOf 280; (90,96,18,30); (180,40,16,86) ] []) (16,125,1) (180,30,16,10) [ (80,"Basher"); (165,"Climber") ]
check "8 mine+bash"   (build 260 [ groundOf 260; (0,60,120,90); (190,96,18,30) ] []) (16,59,1)  (240,116,16,10) [ (60,"Miner"); (180,"Basher") ]
check "9 mine+bash+build" (build 600 [ groundOf 600; (0,60,150,90); (320,96,18,30) ] [ ("lava",430,126,12,24) ]) (16,59,1) (560,116,16,10) [ (90,"Miner"); (310,"Basher"); (429,"Builder") ]
check "10 build+build" (build 360 [ groundOf 360 ] [ ("steel",60,126,40,24); ("lava",120,126,12,24); ("water",210,126,12,24) ]) (16,125,1) (332,116,16,10) [ (119,"Builder"); (209,"Builder") ]
