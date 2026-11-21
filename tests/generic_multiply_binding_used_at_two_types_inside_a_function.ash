// expect: 36 2.25
let square x = x * x

let describe n =
    Ashes.Text.fromInt(square(n)) + " " + Ashes.Text.fromFloat(square(1.5))

6
|> describe
|> Ashes.IO.print
