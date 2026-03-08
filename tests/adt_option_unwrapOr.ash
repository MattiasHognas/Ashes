// expect: 10
type Option =
    | None
    | Some(T)

let unwrapOr = 
    fun (opt) -> 
        fun (def) -> 
            match opt with
                | None -> def
                | Some(x) -> x
in Ashes.IO.print(unwrapOr(None)(10))
