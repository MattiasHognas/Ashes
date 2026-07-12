let map f value =
    match value with
        | None -> None
        | Some(inner) -> Some(f(inner))

let flatMap f value =
    match value with
        | None -> None
        | Some(inner) -> f(inner)

let getOrElse fallback value =
    match value with
        | None -> fallback
        | Some(inner) -> inner

let default = getOrElse

let unwrapOr = getOrElse

let isSome value =
    match value with
        | None -> false
        | Some(_) -> true

let isNone value =
    match value with
        | None -> true
        | Some(_) -> false
