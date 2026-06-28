let map = 
    fun (f) -> 
        fun (value) -> 
            match value with
                | Ok(inner) -> Ok(f(inner))
                | Error(err) -> Error(err)

let flatMap = 
    fun (f) -> 
        fun (value) -> 
            match value with
                | Ok(inner) -> f(inner)
                | Error(err) -> Error(err)

let bind = flatMap

let mapError = 
    fun (f) -> 
        fun (value) -> 
            match value with
                | Ok(inner) -> Ok(inner)
                | Error(err) -> Error(f(err))

let getOrElse = 
    fun (fallback) -> 
        fun (value) -> 
            match value with
                | Ok(inner) -> inner
                | Error(_) -> fallback

let default = getOrElse

let isOk = 
    fun (value) -> 
        match value with
            | Ok(_) -> true
            | Error(_) -> false

let isError = 
    fun (value) -> 
        match value with
            | Ok(_) -> false
            | Error(_) -> true
