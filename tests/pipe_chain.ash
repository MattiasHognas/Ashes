// expect: 4
let inc x = x + 1
in 
    let double x = x + x
    in 
        1
        |> inc
        |> double
        |> Ashes.IO.print
