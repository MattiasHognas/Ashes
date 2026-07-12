// expect: 3
let inc x = x + 1
in
    2
    |> inc
    |> Ashes.IO.print
