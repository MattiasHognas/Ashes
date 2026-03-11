let map = 
    fun (f) -> 
        fun (value) -> 
            match value with
                | Ok(inner) -> Ok(f(inner))
                | Error(err) -> Error(err)
in 
    let bind = 
        fun (f) -> 
            fun (value) -> 
                match value with
                    | Ok(inner) -> f(inner)
                    | Error(err) -> Error(err)
    in 
        let flatMap = bind
        in 
            let mapError = 
                fun (f) -> 
                    fun (value) -> 
                        match value with
                            | Ok(inner) -> Ok(inner)
                            | Error(err) -> Error(f(err))
            in 
                let default = 
                    fun (fallback) -> 
                        fun (value) -> 
                            match value with
                                | Ok(inner) -> inner
                                | Error(_) -> fallback
                in 
                    let isOk = 
                        fun (value) -> 
                            match value with
                                | Ok(_) -> true
                                | Error(_) -> false
                    in 
                        let isError = 
                            fun (value) -> 
                                match value with
                                    | Ok(_) -> false
                                    | Error(_) -> true
                        in isError
