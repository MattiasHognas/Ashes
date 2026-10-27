// expect: 291
let parts (slashed: Str) =
    2
    |> Ashes.Text.drop(slashed)
    |> Ashes.Internal.deepCopy

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + Ashes.Text.length(parts("C:/a/b" + Ashes.Text.fromInt(n))))

0
|> rounds(50)
|> Ashes.IO.print
