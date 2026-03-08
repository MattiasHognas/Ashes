// expect: 6
let f = 
    fun (a) -> 
        fun (b) -> 
            fun (c) -> a + b + c
in 
    let g = f(1)(2)
    in 
        3
        |> g
        |> Ashes.IO.print
