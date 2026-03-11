import Ashes.List
let inc = 
    fun (x) -> x + 1
in 
    let digits = 
        fun (acc) -> 
            fun (x) -> acc * 10 + x
    in 
        [1, 2, 3]
        |> List.map(inc)
        |> List.reverse
        |> List.fold(digits)(0)
        |> Ashes.IO.print
