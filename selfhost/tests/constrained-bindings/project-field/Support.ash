export (
    value findIndexed,
)

let recursive findIndexed key env =
    match env with
        | [] -> None
        | (boundKey, value) :: rest ->
            if boundKey == key
            then Some(value)
            else findIndexed(key)(rest)
