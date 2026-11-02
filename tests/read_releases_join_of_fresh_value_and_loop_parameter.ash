// expect: 3995
let recursive widen (n: Int) (text: Str) =
    if n == 0
    then text
    else widen(n - 1)(text + text)

let recursive loop (n: Int) (text: Str) (total: Int) =
    if n == 0
    then total
    else
        loop(n - 1)(text)(total + Ashes.Text.byteLength(if n % 2 == 0
        then
            n
            |> Ashes.Text.fromInt
            |> widen(6)
        else text))

let recursive rounds (k: Int) (acc: Int) =
    if k == 0
    then acc
    else rounds(k - 1)(acc + loop(10)("ab" + Ashes.Text.fromInt(k))(0))

0
|> rounds(10)
|> Ashes.IO.print
