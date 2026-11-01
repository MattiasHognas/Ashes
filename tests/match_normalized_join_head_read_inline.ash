// expect: 11|11
let probe (n: Int) =
    match [Ashes.Text.fromInt(n), "a"] with
        | x :: _ -> x
        | [] -> ""

let recursive inline (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        inline(n - 1)(acc + Ashes.Text.byteLength(match [Ashes.Text.fromInt(n), "a"] with
            | x :: _ -> x
            | [] -> ""))

let recursive called (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        called(n - 1)(acc + Ashes.Text.byteLength(probe(n)))
in
    Ashes.IO.print(Ashes.Text.fromInt(inline(10)(0)) + "|" + Ashes.Text.fromInt(called(10)(0)))
