// expect: 150
type Box =
    | pairs: List((Int, Int))
    | count: Int

let shift (box: Box) =
    Ashes.Collection.List.map(given (p: (Int, Int)) ->
        match p with
            | (a, c) -> (a + 1, c))(box.pairs)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + Ashes.Collection.List.length(shift(Box(pairs = [(n, 1), (2, n), (3, 3)], count = n))))

0
|> rounds(50)
|> Ashes.IO.print
