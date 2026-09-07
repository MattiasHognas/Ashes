// expect: 3
let recursive inc x = x + 1
in
    2
    |> inc
    |> Ashes.IO.print
