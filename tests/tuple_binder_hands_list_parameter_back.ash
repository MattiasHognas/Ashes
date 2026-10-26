// expect: 60

type Kept =
    | state: List(Int)
    | temp: Int

let recursive peel (items: List(List(Int))) (kept: List(Int)) (acc: Int) =
    match (items, kept) with
        | ((x :: xs) :: rest, same) -> peel(xs :: rest)(same)(acc + x)
        | (_, k) -> acc + Ashes.Collection.List.length(k)

let recursive count (items: List(Int)) (kept: Kept) (acc: Int) =
    match (items, kept) with
        | (all, Kept { state = st, temp = t }) ->
            if t > 0
            then count(all)(Kept(state = st, temp = t - 1))(acc + t)
            else acc + Ashes.Collection.List.length(all) + Ashes.Collection.List.length(st)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + peel([[3, 2, n], [4, n]])([n, 2])(0) + count([n, 2, 3])(Kept(state = [n, 7], temp = 3))(0))

0
|> rounds(3)
|> Ashes.IO.print
