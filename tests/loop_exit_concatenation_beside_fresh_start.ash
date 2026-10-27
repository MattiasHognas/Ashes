// expect: 3000
let recursive build (table: List(Str)) (remaining: Int) (acc: Str) =
    if remaining <= 0
    then
        acc + Ashes.Text.fromInt(Ashes.Collection.List.length(table))
    else
        if remaining == 1
        then build(table)(remaining - 1)(acc + "x")
        else
            let table = ["next", Ashes.Text.fromInt(remaining)]
            in build(table)(remaining - 1)(acc + "x")

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + Ashes.Text.byteLength(build([Ashes.Text.fromInt(n)])(5)("")))

0
|> rounds(500)
|> Ashes.IO.print
