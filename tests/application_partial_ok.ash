// expect: 3
let add = 
    fun (x) -> 
        fun (y) -> x + y
in 
    let add1 = add(1)
    in 
        2
        |> add1
        |> Ashes.IO.print
