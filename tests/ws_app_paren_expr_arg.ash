// expect: 6
let f x = x
in
    1 + 2 + 3
    |> f
    |> Ashes.IO.print
