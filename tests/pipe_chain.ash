// expect: 4
let inc = 
    fun (x) -> x + 1
in 
    let double = 
        fun (x) -> x + x
    in 
        1
        |> inc
        |> double
        |> Ashes.IO.print
