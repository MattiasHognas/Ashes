// expect: 74

type Kept =
    | state: List(Int)
    | temp: Int

let recursive walk (items: List(List(Int))) (kept: Kept) (acc: Int) =
    match (items, kept) with
        | ((x :: xs) :: rest, same) ->
            if x > 0
            then walk((x - 1 :: xs) :: rest)(same)(acc + x)
            else walk(xs :: rest)(same)(acc)
        | (_, Kept { state = st, temp = k }) -> acc + k + Ashes.Collection.List.length(st)

let recursive chain (items: List(List(Int))) (acc: Int) =
    match items with
        | (x :: xs) :: rest ->
            if x > 0
            then chain((x - 1 :: 0 :: xs) :: rest)(acc + x)
            else chain(xs :: rest)(acc)
        | [] :: rest -> chain(rest)(acc)
        | [] -> acc

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + walk([[3, 2, n], [4, n]])(Kept(state = [n, 7, 8], temp = n))(0) + chain([[2, n], [1]])(0))

0
|> rounds(3)
|> Ashes.IO.print
