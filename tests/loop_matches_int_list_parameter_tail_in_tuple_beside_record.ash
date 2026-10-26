// expect: 1015000
type Lowered =
    | state: List(Int)
    | temp: Int

let recursive close (pending: List(Int)) (lowered: Lowered) =
    match (pending, lowered) with
        | ([], _lowered) -> lowered
        | (n :: rest, Lowered { state = bodyState, temp = temp }) -> close(rest)(Lowered(state = bodyState, temp = temp + n))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match close([n, 2, 3, 4, 5])(Lowered(state = [], temp = n)) with
            | Lowered { state = st, temp = t } -> rounds(n - 1)(total + t + Ashes.Collection.List.length(st))

0
|> rounds(1000)
|> Ashes.IO.print
