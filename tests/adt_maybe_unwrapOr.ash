// expect: 10
let unwrapOr = 
    fun (opt) -> 
        fun (def) -> 
            match opt with
                | None -> def
                | Some(x) -> x
in Ashes.IO.print(unwrapOr(None)(10))
