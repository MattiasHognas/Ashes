// expect: 5
let add x y = x + y
in 
    let add1 = add(1)
    in 
        4
        |> add1
        |> Ashes.IO.print
