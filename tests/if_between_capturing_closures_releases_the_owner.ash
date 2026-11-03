// expect: z5q,z4q,3qz,2qz,1qz,3q!
let tag (n: Int) = Ashes.Text.fromInt(n) + "q"

let pick (n: Int) =
    (let s = tag(n)
    in
        if n > 3
        then
            given (x) -> x + s
        else
            given (x) -> s + x)

let only (n: Int) =
    (let s = tag(n)
    in
        if n > 3
        then
            given (x) -> x + "?"
        else
            given (x) -> s + x)

let recursive loop (n: Int) (acc: Str) =
    if n == 0
    then acc
    else loop(n - 1)(acc + pick(n)("z") + ",")

loop(5)("") + only(3)("!") |> Ashes.IO.print
