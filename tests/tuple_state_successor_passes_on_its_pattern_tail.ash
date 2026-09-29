// A loop matches its `(List(Str), Int)` state directly and passes the state's tail on in a fresh
// tuple, returning the state beside the names it collected.
// expect: 9
let recursive parameters (reversed: List(Str)) (state: (List(Str), Int)) =
    match state with
        | ("=" :: _, _count) -> (reversed, state)
        | (token :: tail, count) -> parameters(token :: reversed)((tail, count + 1))
        | ([], _count) -> (reversed, state)

let run (seed: Int) =
    match parameters([])((["smoke", "coal", "smoke", "=", "ash"], seed)) with
        | (names, _after) -> Ashes.Collection.List.length(names)

let recursive loop (remaining: Int) (checksum: Int) =
    if remaining == 0
    then checksum
    else loop(remaining - 1)(checksum + run(2))

0
|> loop(3)
|> Ashes.IO.print
