let map = 
    fun (f) -> 
        fun (value) -> 
            match value with
                | None -> None
                | Some(inner) -> Some(f(inner))
in 
    let flatMap = 
        fun (f) -> 
            fun (value) -> 
                match value with
                    | None -> None
                    | Some(inner) -> f(inner)
    in 
        let getOrElse = 
            fun (fallback) -> 
                fun (value) -> 
                    match value with
                        | None -> fallback
                        | Some(inner) -> inner
        in 
            let default = getOrElse
            in 
                let unwrapOr = getOrElse
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
                        in isNone
