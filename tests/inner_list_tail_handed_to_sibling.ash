// expect: 101

type Kept =
    | state: List(Int)
    | temp: Int

let recursive walk (items: List(List(Int))) (spare: List(Int)) (acc: Int) =
    match (items, spare) with
        | ((x :: xs) :: rest, s) ->
            if x > 0
            then walk((x - 1 :: s) :: rest)(xs)(acc + x)
            else walk(xs :: rest)(s)(acc)
        | (_, s) -> acc + Ashes.Collection.List.length(s)

let recursive keep (items: List(List(Int))) (spare: List(Int)) (kept: Kept) (acc: Int) =
    match (items, spare, kept) with
        | ((x :: xs) :: rest, s, same) ->
            if x > 0
            then keep((x - 1 :: []) :: rest)(xs)(same)(acc + x)
            else keep(xs :: rest)(s)(same)(acc)
        | (_, s, Kept { state = st, temp = k }) -> acc + k + Ashes.Collection.List.length(s) + Ashes.Collection.List.length(st)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + walk([[3, 2, n], [4, n]])([n, 7, 8])(0) + keep([[3, 2, n], [4, n]])([n, 7])(Kept(state = [n], temp = n))(0))

0
|> rounds(2)
|> Ashes.IO.print
