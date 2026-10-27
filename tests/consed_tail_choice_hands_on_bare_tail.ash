// expect: 3150
let recursive len (xs: List(Int)) (n: Int) =
    match xs with
        | [] -> n
        | _ :: rest -> len(rest)(n + 1)

type Kept =
    | state: List(Int)
    | temp: Int

let recursive walk (items: List(List(Int))) (kept: Kept) (acc: Int) =
    match (items, kept) with
        | ((x :: xs) :: rest, same) ->
            if x > 0
            then
                walk(if x > 2
                then xs :: rest
                else rest)(same)(acc + x)
            else walk(xs :: rest)(same)(acc)
        | (_, Kept { state = st, temp = k }) -> acc + k + len(st)(0)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + walk([[3, 2, n], [4, n]])(Kept(state = [n, 7, 8], temp = n))(0))

0
|> rounds(50)
|> Ashes.IO.print
