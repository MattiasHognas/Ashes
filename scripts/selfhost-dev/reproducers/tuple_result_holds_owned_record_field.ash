type State =
    | items: List(Str)
    | count: Int

let step (s: State) = State(items = "a" + Ashes.Text.fromInt(s.count) :: s.items, count = s.count + 1)

let summarize (s: State) =
    (let next = step(s)
    in (next.items, next.count > 2))

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        match summarize(State(items = ["x", "y"], count = remaining)) with
            | (texts, _big) -> loop(remaining - 1)(total + Ashes.Collection.List.length(texts))

0
|> loop(ROUNDS)
|> Ashes.IO.print
