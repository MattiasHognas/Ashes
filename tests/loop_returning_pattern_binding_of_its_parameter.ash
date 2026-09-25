// expect: 2000
type T =
    | Fn(T, T)
    | Named(Int, Str, List(T))
    | Var(Int)

let recursive resultType (t: T) =
    match t with
        | Fn(_parameter, result) -> resultType(result)
        | other -> other

let recursive seenName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || seenName(name)(rest)

let recursive freeTypes (items: List(T)) (seen: List(Str)) =
    match items with
        | [] -> []
        | body :: rest ->
            match resultType(body) with
                | Named(_id, name, []) as named ->
                    if seenName(name)(seen)
                    then freeTypes(rest)(seen)
                    else (name, named) :: freeTypes(rest)(name :: seen)
                | _ -> freeTypes(rest)(seen)

let recursive countItems (values: List((Str, T))) (total: Int) =
    match values with
        | [] -> total
        | _ :: rest -> countItems(rest)(total + 1)

let items =
    [[Var(1)]
    |> Named(0)("Maybe")
    |> Fn(Var(1)), Named(0)("Int")([]), []
    |> Named(0)("Int")
    |> Fn(Var(2)), Named(0)("List")([Var(3)])]

let recursive repeat (times: Int) (total: Int) =
    if times == 0
    then total
    else
        repeat(times - 1)(total + countItems(freeTypes(items)([]))(0))

0
|> repeat(2000)
|> Ashes.IO.print
