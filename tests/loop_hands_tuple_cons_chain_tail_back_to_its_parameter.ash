// expect: 5010000
type Kept =
    | state: List(Int)
    | temp: Int

let recursive pairs (items: List(Int)) (kept: Kept) (acc: Int) =
    match (items, kept) with
        | (a :: b :: rest, same) -> pairs(rest)(same)(acc + a * b)
        | (_, Kept { state = st, temp = k }) -> acc + k + Ashes.Collection.List.length(st)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + pairs([1, 2, n, 4, 5, n, 7])(Kept(state = [n, 7, 8], temp = n))(0))

0
|> rounds(1000)
|> Ashes.IO.print
