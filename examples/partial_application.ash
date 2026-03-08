let add = 
    fun (x) -> 
        fun (y) -> x + y
in 
    let add1 = add(1)
    in 
        41
        |> add1
        |> Ashes.IO.print
