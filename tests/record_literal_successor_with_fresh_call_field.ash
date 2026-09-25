// A loop whose record state is rebuilt as a literal at the self call, one field holding a fresh
// call result: the back edge's copy of the dying record releases that field once, not twice.
// expect: 84000
type State =
    | bindings: List(Str)
    | label: Str
    | counter: Int

let grow (value: Int) (old: List(Str)) =
    if value % 3 == 0
    then ["x", "y"]
    else "z" :: old

let recursive count (items: List(Str)) (total: Int) =
    match items with
        | [] -> total
        | _ :: rest -> count(rest)(total + 1)

let recursive lowerItems (items: List(Int)) (state: State) =
    match items with
        | [] -> state.counter + count(state.bindings)(0)
        | value :: rest -> lowerItems(rest)(State(bindings = grow(value)(state.bindings), label = state.label, counter = state.counter + 1))

let recursive numbers (n: Int) (built: List(Int)) =
    if n == 0
    then built
    else numbers(n - 1)(n :: built)

let program = numbers(80)([])

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + lowerItems(program)(State(bindings = ["a", "b"], label = "s", counter = 0)))

0
|> rounds(1000)
|> Ashes.IO.print
