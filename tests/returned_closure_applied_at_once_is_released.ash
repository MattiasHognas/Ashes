// expect: a3q,a2q,a1q,
let tag (n: Int) = Ashes.Text.fromInt(n) + "q"

let make (n: Int) =
    (let s = tag(n)
    in
        given (x) -> x + s)

let recursive loop (n: Int) (acc: Str) =
    if n == 0
    then acc
    else loop(n - 1)(acc + make(n)("a") + ",")

""
|> loop(3)
|> Ashes.IO.print
