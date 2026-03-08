type Option(T) =
    | None
    | Some(T)

let map = 
    fun (f) -> 
        fun (value) -> 
            match value with
                | None -> None
                | Some(inner) -> Some(f(inner))
in 
    let default = 
        fun (fallback) -> 
            fun (value) -> 
                match value with
                    | None -> fallback
                    | Some(inner) -> inner
    in 
        let isSome = 
            fun (value) -> 
                match value with
                    | None -> false
                    | Some(_) -> true
        in 
            let isNone = 
                fun (value) -> 
                    match value with
                        | None -> true
                        | Some(_) -> false
            in 
                let unwrapOr = 
                    fun (fallback) -> 
                        fun (value) -> 
                            match value with
                                | None -> fallback
                                | Some(inner) -> inner
                in unwrapOr
