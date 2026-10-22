// A loop threads a `(List(Str), Int)` state and returns it inside its result pair: the state is
// owned by the loop, each back edge releasing the predecessor together with its list.
// expect: 9
type alias Tokens = (List(Str), Int)

let advance (state: Tokens) =
    match state with
        | (token :: tail, count) -> (token, (tail, count + 1))
        | ([], _count) -> ("", state)

let currentIs (text: Str) (state: Tokens) =
    match state with
        | (token :: _, _count) -> token == text
        | ([], _count) -> false

let recursive parameters (reversed: List(Str)) (state: Tokens) =
    if currentIs("=")(state)
    then (reversed, state)
    else
        match advance(state) with
            | (name, afterName) -> parameters(name :: reversed)(afterName)

let run (seed: Int) =
    match parameters([])((["smoke", "coal", "smoke", "=", "ash"], seed)) with
        | (names, (_rest, count)) -> Ashes.Collection.List.length(names) + count

let recursive loop (remaining: Int) (checksum: Int) =
    if remaining == 0
    then checksum
    else loop(remaining - 1)(checksum + run(0))

Ashes.IO.print(loop(3)(0) / 2)
