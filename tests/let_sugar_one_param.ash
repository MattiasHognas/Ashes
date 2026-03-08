// expect: 42
let id x = x
in 
    42
    |> id
    |> Ashes.IO.print
