let map f value =
    match value with
        | Ok(inner) ->
            inner
            |> f
            |> Ok
        | Error(err) -> Error(err)

let flatMap f value =
    match value with
        | Ok(inner) -> f(inner)
        | Error(err) -> Error(err)

let bind = flatMap

let mapError f value =
    match value with
        | Ok(inner) -> Ok(inner)
        | Error(err) ->
            err
            |> f
            |> Error

let getOrElse fallback value =
    match value with
        | Ok(inner) -> inner
        | Error(_) -> fallback

let default = getOrElse

let isOk value =
    match value with
        | Ok(_) -> true
        | Error(_) -> false

let isError value =
    match value with
        | Ok(_) -> false
        | Error(_) -> true
