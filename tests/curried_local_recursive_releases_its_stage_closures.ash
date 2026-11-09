// expect: 3
let recursive build (n: Int) =
    if n == 0
    then []
    else n :: build(n - 1)

let recursive member (x: Int) (xs: List(Int)) =
    match xs with
        | [] -> false
        | y :: rest -> y == x || member(x)(rest)

let probe (n: Int) (xs: List(Int)) =
    (let seen = build(3)
    in
        let recursive find items idx acc =
            match items with
                | [] -> acc
                | x :: tail ->
                    if member(x)(seen)
                    then find(tail)(idx + 1)(acc + idx)
                    else find(tail)(idx + 1)(acc)
        in find(xs)(0)(0))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n)([n, 2]))

0
|> loop(3)
|> Ashes.IO.print
