// A loop whose exit arm builds a record holding its record parameter, returned and copied by the
// result normalization; another loop that builds such a record only to match its scalar field. The
// copy and the match borrow the parameter, which the loop's exit then releases.
// expect: 12
type State =
    | instructions: List(Int)
    | counter: Int

type Arguments =
    | state: State
    | count: Int

let recursive lowerArguments (arguments: List(Int)) (state: State) =
    match arguments with
        | [] -> Arguments(state = state, count = 0)
        | _argument :: rest -> lowerArguments(rest)(state)

let lowerCall (arguments: List(Int)) (state: State) =
    match lowerArguments(arguments)(state) with
        | Arguments { state = argumentState, count = count } -> argumentState.counter + count

let recursive countArguments (arguments: List(Int)) (state: State) =
    match arguments with
        | [] ->
            match Arguments(state = state, count = 0) with
                | Arguments { count = count } -> count + state.counter
        | _argument :: rest -> countArguments(rest)(state)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + lowerCall([1, 2, 3, 4, 5])(State(instructions = [n], counter = n)) + countArguments([1, 2, 3])(State(instructions = [n], counter = n)))

0
|> rounds(3)
|> Ashes.IO.print
