// expect: 127250

type Close =
    | closeName: Int
    | closeBindings: List(Str)

type State =
    | names: List(Str)
    | counter: Int

let recursive countCloses (items: List(Close)) (total: Int) =
    match items with
        | [] -> total
        | _ :: rest -> countCloses(rest)(total + 1)

let recursive lowerItems (items: List(Int)) (pending: List(Close)) (state: State) =
    match items with
        | [] -> state.counter + countCloses(pending)(0)
        | _value :: rest -> lowerItems(rest)(Close(closeName = 0, closeBindings = state.names) :: pending)((state with counter = state.counter + 1))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + lowerItems([2, 4])([])(State(names = ["cinder", "cinder"], counter = n)))

0
|> rounds(500)
|> Ashes.IO.print
