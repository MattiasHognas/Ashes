// expect: 3250

type HandOff =
    | pendingRootSlot: Maybe(Int)
    | mayReach: Bool

type State =
    | counter: Int
    | instructions: List(Int)
    | pending: List((Int, Int))

let emitFlag (closureTemp: Int) (state: State) =
    (state with counter = state.counter + 5, instructions = [closureTemp, state.counter]) |> (given (flagged) -> (flagged, state.counter + 4))

let prepareCopyTypeArgument (handOff: HandOff) (functionTemp: Int) (argumentTemp: Int) (state: State) =
    match emitFlag(functionTemp)(state) with
        | (flagged, acceptsFlagTemp) -> (flagged, argumentTemp, acceptsFlagTemp, None)

let recursive rounds (n: Int) (state: State) (total: Int) =
    if n == 0
    then total + state.counter
    else
        match prepareCopyTypeArgument(HandOff(pendingRootSlot = Some(n % 7), mayReach = n % 2 == 0))(n)(n + 1)(state) with
            | (next, passed, flag, _h) -> rounds(n - 1)(next)(total + passed % 3 + flag % 2)

0
|> rounds(500)(State(counter = 0, instructions = [], pending = []))
|> Ashes.IO.print
