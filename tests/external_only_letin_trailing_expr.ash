// expect: 5

external strlen(Str) -> Int

let length = strlen("Ashes")
in
    length
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
