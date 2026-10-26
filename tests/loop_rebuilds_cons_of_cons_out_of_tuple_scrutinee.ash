// expect: 1516500
type Kept =
    | state: List(Int)
    | temp: Int

let recursive walk (items: List(List(Int))) (kept: Kept) (acc: Int) =
    match (items, kept) with
        | ([], Kept { state = st, temp = k }) -> acc + k + Ashes.Collection.List.length(st)
        | ([] :: rest, same) -> walk(rest)(same)(acc)
        | ((x :: xs) :: rest, same) -> walk(xs :: rest)(same)(acc + x)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + walk([[1, 2, n], [n, 4], [5]])(Kept(state = [n, 7, 8], temp = n))(0))

0
|> rounds(1000)
|> Ashes.IO.print
