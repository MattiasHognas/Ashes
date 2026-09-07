// expect: 0
let recursive f n acc =
    if acc == 0
    then 0
    else f(0)(acc - 1)

5
|> f(3)
|> Ashes.Text.fromInt
|> Ashes.IO.print
