// expect: 7
let add x y = x + y
in 
    4
    |> add(3)
    |> Ashes.IO.print
