// expect: 2492
let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + Ashes.Text.length(Ashes.IO.Path.normalize(Ashes.IO.Path.Windows)("C:\\tmp\\a" + Ashes.Text.fromInt(n) + "\\b")))

0
|> rounds(200)
|> Ashes.IO.print
