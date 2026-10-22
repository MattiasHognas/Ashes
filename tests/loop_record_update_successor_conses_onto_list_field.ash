// A loop whose successor is an update of its own record parameter, consing onto a list field and
// keeping another: the back edge's copy borrows the fields the update read out of the parameter,
// and the loop runs the same shared input list round after round.
// expect: 486
type State =
    | instructions: List(Int)
    | labels: List(Str)
    | counter: Int

let recursive countInts (items: List(Int)) (total: Int) =
    match items with
        | [] -> total
        | _ :: tail -> countInts(tail)(total + 1)

let recursive countStrs (items: List(Str)) (total: Int) =
    match items with
        | [] -> total
        | _ :: tail -> countStrs(tail)(total + 1)

let recursive lowerItems (items: List(Int)) (state: State) =
    match items with
        | [] -> state.counter + countInts(state.instructions)(0) + countStrs(state.labels)(0)
        | value :: rest -> lowerItems(rest)((state with instructions = value :: state.instructions, counter = state.counter + 1))

let recursive numbers (n: Int) (built: List(Int)) =
    if n == 0
    then built
    else numbers(n - 1)(n :: built)

let program = numbers(80)([])

let recursive repeat (times: Int) (total: Int) =
    if times == 0
    then total
    else repeat(times - 1)(total + lowerItems(program)(State(instructions = [], labels = ["a", "b"], counter = 0)))

0
|> repeat(3)
|> Ashes.IO.print
