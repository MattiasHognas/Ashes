export (
    value uniquePaths,
)

let recursive containsPath path paths =
    match paths with
        | [] -> false
        | candidate :: rest ->
            if candidate == path
            then true
            else containsPath(path)(rest)

let recursive uniquePaths paths seen =
    match paths with
        | [] -> []
        | path :: rest ->
            if containsPath(path)(seen)
            then uniquePaths(rest)(seen)
            else path :: uniquePaths(rest)(path :: seen)
