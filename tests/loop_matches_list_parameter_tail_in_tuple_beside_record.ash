// expect: 1009000
type Close =
    | span: Int
    | outer: List(Int)

type Lowered =
    | state: List(Int)
    | temp: Int

let recursive close (pending: List(Close)) (lowered: Lowered) =
    match (pending, lowered) with
        | ([], _lowered) -> lowered
        | (item :: rest, Lowered { state = bodyState, temp = temp }) -> close(rest)(Lowered(state = item.span :: bodyState, temp = temp + item.span))

let closes (n: Int) = [Close(span = n, outer = [n]), Close(span = 2, outer = []), Close(span = 3, outer = [n, n])]

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match close(closes(n))(Lowered(state = [], temp = n)) with
            | Lowered { state = st, temp = t } -> rounds(n - 1)(total + t + Ashes.Collection.List.length(st))

0
|> rounds(1000)
|> Ashes.IO.print
