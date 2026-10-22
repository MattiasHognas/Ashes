// expect: 2000

type State =
    | substitution: List(Int)
    | counter: Int

let step (value: Int) (state: State) = state with substitution = value :: state.substitution

let recursive walk items state =
    match items with
        | [] -> (state, 0)
        | value :: rest ->
            state
            |> step(value)
            |> walk(rest)

let recursive count (values: List(Int)) (total: Int) =
    match values with
        | [] -> total
        | _ :: rest -> count(rest)(total + 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match walk([1, 2, 3])(State(substitution = [n], counter = n)) with
            | (state, extra) -> rounds(n - 1)(total + extra + count(state.substitution)(0))

0
|> rounds(500)
|> Ashes.IO.print
