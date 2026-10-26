// expect: 1517500
type Kept =
    | state: List(Int)
    | temp: Int

let recursive close (pending: List(Int)) (kept: Kept) (acc: Int) =
    match (pending, kept) with
        | ([], Kept { state = st, temp = t }) -> acc + t + Ashes.Collection.List.length(st)
        | (n :: rest, same) -> close(rest)(same)(acc + n)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + close([1, 2, 3, 4, n])(Kept(state = [n, 7, 8, 9, 10, 11], temp = n))(n))

0
|> rounds(1000)
|> Ashes.IO.print
