// expect: 65
// handOff binds its result as an arena record and returns either an update of it that copies the
// retained Maybe field or one that replaces it with None. The record's own reference to the field
// is released once after the result's copy on both arms: the copy retains the field where it kept
// it, and nothing else holds it where it was replaced.
type Frame =
    | bodyLabel: Str
    | parameterSlots: List((Str, Int))

type Loop =
    | name: Str
    | cached: Maybe(Int)

type Group =
    | tcoLoop: Maybe(Loop)
    | tcoLoopFrame: Maybe(Frame)

type State =
    | tcoState: Group
    | names: List(Str)

let stateTcoLoop (state: State) =
    (let group = state.tcoState
    in group.tcoLoop)

let stateTcoLoopFrame (state: State) =
    (let group = state.tcoState
    in group.tcoLoopFrame)

type Expr =
    | ExprVar(Str)
    | ExprNum(Int)

let recursive slotIn (name: Str) (entries: List((Str, Int))) =
    match entries with
        | [] -> None
        | (key, slot) :: rest ->
            if key == name
            then Some(slot)
            else slotIn(name)(rest)

let patternBindingRootSlot (name: Str) (loop: Loop) = loop.cached

let argumentRootSlotOf (argument: Expr) (state: State) =
    match (stateTcoLoopFrame(state), stateTcoLoop(state), argument) with
        | (Some(frame), Some(loop), ExprVar(name)) ->
            match slotIn(name)(frame.parameterSlots) with
                | Some(slot) -> Some(slot)
                | None -> patternBindingRootSlot(name)(loop)
        | _ -> None

type HandOff =
    | borrowsOnly: Bool
    | runtimeArgument: Bool
    | mayReach: Bool
    | borrowedReach: Bool
    | pendingRootSlot: Maybe(Int)

let handOff (argument: Expr) (state: State) =
    match (Ashes.Collection.List.length(state.names) > 3, argumentRootSlotOf(argument)(state), Ashes.Collection.List.length(state.names) > 5) with
        | (runtimeArgument, rootSlot, fresh) ->
            let built =
                HandOff(
                    borrowsOnly = false,
                    runtimeArgument = runtimeArgument,
                    mayReach = rootSlot != None,
                    borrowedReach = rootSlot != None && !fresh,
                    pendingRootSlot = if runtimeArgument
                    then None
                    else rootSlot
                )
            in
                match built.pendingRootSlot with
                    | Some(s) ->
                        if s > 2
                        then built with borrowsOnly = true
                        else built with pendingRootSlot = None
                    | None -> built

let makeState (n: Int) =
    State(
        tcoState = Group(tcoLoop = Some(Loop(name = "l" + Ashes.Text.fromInt(n), cached = if n % 2 == 0
        then None
        else Some(n))), tcoLoopFrame = Some(Frame(bodyLabel = "body", parameterSlots = [("a", 0)]))),
        names = ["a" + Ashes.Text.fromInt(n)]
    )

let score (handOff: HandOff) =
    match handOff.pendingRootSlot with
        | Some(slot) -> slot + 1
        | None ->
            if handOff.borrowedReach
            then 2
            else 3

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        let state = makeState(n)
        in
            loop(n - 1)(acc + score(handOff(ExprVar("z"))(state)) + score(handOff(ExprVar("a"))(state)))
in
    0
    |> loop(10)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
