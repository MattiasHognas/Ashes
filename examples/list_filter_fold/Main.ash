import Ashes.List
let keepLarge x = x >= 3
in 
    let add acc x = acc + x
    in 
        [1, 2, 3, 4]
        |> List.filter(keepLarge)
        |> List.fold(add)(0)
        |> Ashes.IO.print
