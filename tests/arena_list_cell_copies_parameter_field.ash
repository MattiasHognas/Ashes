// expect: 127750

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
        | value :: rest ->
            if value % 2 == 0
            then lowerItems(rest)(Close(closeName = value, closeBindings = state.names) :: pending)((state with counter = state.counter + 1))
            else
                match pending with
                    | _ :: older -> lowerItems(rest)(older)(state)
                    | [] -> lowerItems(rest)([])(state)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + lowerItems([2, 7, 4, 4])([])(State(names = ["a", "b"], counter = n)))

0
|> rounds(500)
|> Ashes.IO.print
