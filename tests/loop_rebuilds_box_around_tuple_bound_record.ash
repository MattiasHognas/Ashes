// expect: 1510500
type Kept =
    | state: List(Int)
    | temp: Int

type Box =
    | Box(Kept)

let recursive close (pending: List(Int)) (box: Box) (acc: Int) =
    match (pending, box) with
        | ([], Box(Kept { state = st, temp = t })) -> acc + t + Ashes.Collection.List.length(st)
        | (n :: rest, Box(inner)) -> close(rest)(Box(inner))(acc + n)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + close([1, 2, 3, n])(Box(Kept(state = [n, 7, 8], temp = n)))(n))

0
|> rounds(1000)
|> Ashes.IO.print
