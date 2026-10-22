// An owned record passed into a generic forwarder whose result is a closure call of unresolved
// type: the innermost contract result says it holds nothing of its arguments, so the forwarder
// releases the record.
// expect: 40
type State =
    | names: List(Str)
    | nextTemp: Int

type FreshTemp =
    | state: State
    | temp: Int

let freshTemp (state: State) =
    match state with
        | State { nextTemp = nextTemp } -> FreshTemp(state = (state with nextTemp = nextTemp + 1), temp = nextTemp)

let emit name (state: State) = state with names = name :: state.names

let lowerLoaded label lowerNext loaded =
    match loaded with
        | FreshTemp { state = state, temp = temp } -> lowerNext(label)(temp)(state)

let loadField (label: Str) lowerNext (state: State) =
    state
    |> freshTemp
    |> lowerLoaded(label)(lowerNext)

let finish label temp state = emit(label + Ashes.Text.fromInt(temp))(state)

let recursive rounds (state: State) (n: Int) =
    if n == 0
    then Ashes.Collection.List.length(state.names)
    else
        rounds(loadField("x")(finish)(state))(n - 1)

State(names = [], nextTemp = 0)
|> rounds
|> (given (f) -> f(40))
|> Ashes.IO.print
