let map = 
    fun (f) -> 
        fun (value) -> 
            match value with
                | None -> None
                | Some(inner) -> Some(f(inner))

let flatMap = 
    fun (f) -> 
        fun (value) -> 
            match value with
                | None -> None
                | Some(inner) -> f(inner)

let getOrElse = 
    fun (fallback) -> 
        fun (value) -> 
            match value with
                | None -> fallback
                | Some(inner) -> inner

let default = getOrElse

let unwrapOr = getOrElse

let isSome = 
    fun (value) -> 
        match value with
            | None -> false
            | Some(_) -> true

let isNone = 
    fun (value) -> 
        match value with
            | None -> true
            | Some(_) -> false
