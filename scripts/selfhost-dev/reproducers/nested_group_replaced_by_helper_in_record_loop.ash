type Inner =
    | span: Maybe(Int)
    | names: List(Str)

type State =
    | inner: Inner
    | count: Int
    | items: List(Str)

let withSpan (value: Maybe(Int)) (state: State) =
    (let group = state.inner
    in state with inner = (group with span = value))

let recursive walk (n: Int) (state: State) =
    if n == 0
    then state.count
    else
        (state with count = state.count + 1)
        |> withSpan(Some(n))
        |> walk(n - 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + walk(20)(State(inner = Inner(span = None, names = ["a"]), count = 0, items = ["b"])))

0
|> rounds(ROUNDS)
|> Ashes.IO.print
