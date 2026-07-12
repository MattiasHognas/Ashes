import Ashes.List
let inc x = x + 1
in
    let add acc x = acc + x
    in
        [1, 2, 3]
        |> List.map(inc)
        |> List.fold(add)(0)
        |> Ashes.IO.print
