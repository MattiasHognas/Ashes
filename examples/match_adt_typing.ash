type Option =
    | None
    | Some(T)

let unwrapOr = 
    fun (opt) -> 
        fun (def) -> 
            match opt with
                | None -> def
                | Some(x) -> x
in 
    let getOrDefault = 
        fun (res) -> 
            fun (def) -> 
                match res with
                    | Ok(v) -> v
                    | Error(_) -> def
    in Ashes.IO.print(unwrapOr(Some(10))(0) + getOrDefault(Ok(5))(0))
