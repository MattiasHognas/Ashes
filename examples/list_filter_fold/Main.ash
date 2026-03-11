import Ashes.List
let keepLarge = 
    fun (x) -> x >= 3
in 
    let add = 
        fun (acc) -> 
            fun (x) -> acc + x
    in 
        [1, 2, 3, 4]
        |> List.filter(keepLarge)
        |> List.fold(add)(0)
        |> Ashes.IO.print
