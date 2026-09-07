// expect: 0
let b = Ashes.Byte.empty(Unit)
in
    b
    |> Ashes.Byte.length
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
