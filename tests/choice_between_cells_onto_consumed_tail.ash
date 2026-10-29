// expect: 52

type Kept =
    | state: List(Int)
    | temp: Int

let recursive walk (items: List(List(Int))) (kept: Kept) (acc: Int) =
    match (items, kept) with
        | ((x :: xs) :: rest, same) ->
            if x > 0
            then
                walk(if x > 2
                then (x - 1 :: xs) :: rest
                else xs :: rest)(same)(acc + x)
            else walk(xs :: rest)(same)(acc)
        | (_, Kept { state = st, temp = k }) -> acc + k + Ashes.Collection.List.length(st)

let recursive skip (items: List(List(Int))) (kept: List(Int)) (acc: Int) =
    match (items, kept) with
        | ((x :: xs) :: rest, same) ->
            if x > 2
            then
                skip(if x > 3
                then (x - 1 :: xs) :: rest
                else rest)(same)(acc + x)
            else skip(xs :: rest)(same)(acc)
        | (_, k) -> acc + Ashes.Collection.List.length(k)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + walk([[3, 2, n], [4, n]])(Kept(state = [n, 7, 8], temp = n))(0) + skip([[5, n], [1, 2]])([n])(0))

0
|> rounds(2)
|> Ashes.IO.print
