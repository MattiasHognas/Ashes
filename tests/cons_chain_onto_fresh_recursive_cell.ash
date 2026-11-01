// expect: 1100
let recursive keepPairs (xs: List(Str)) (seen: List(Str)) =
    match (xs, seen) with
        | (x :: rest, s :: more) ->
            if x == s
            then keepPairs(rest)(more)
            else x :: s :: keepPairs(rest)(more)
        | _ -> seen

let recursive triple (xs: List(Int)) =
    match xs with
        | x :: rest -> x :: x :: x :: triple(rest)
        | [] -> []

let recursive words (n: Int) =
    if n == 0
    then []
    else "w" + Ashes.Text.fromInt(n) :: words(n - 1)

let recursive numbers (n: Int) =
    if n == 0
    then []
    else n :: numbers(n - 1)

let recursive count (xs: List(Str)) =
    match xs with
        | [] -> 0
        | x :: rest -> Ashes.Text.byteLength(x) + count(rest)

let recursive total (xs: List(Int)) =
    match xs with
        | [] -> 0
        | x :: rest -> x + total(rest)

let run (seed: Int) =
    (let a = words(seed % 4 + 3)
    in
        let b =
            keepPairs(words(3))(a)
        in
            count(b) + count(keepPairs(b)(a)) + total(seed % 3 + 1
            |> numbers
            |> triple))

let recursive rounds (n: Int) (sum: Int) =
    if n == 0
    then sum
    else rounds(n - 1)(sum + run(n))

0
|> rounds(30)
|> Ashes.IO.print
