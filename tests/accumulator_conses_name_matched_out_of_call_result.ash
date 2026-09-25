// expect: 3

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

let recursive freeTypes (items: List(T)) (seen: List(Str)) (count: Int) =
    match items with
        | [] -> count
        | body :: rest ->
            match resultType(body) with
                | Named(_id, name, []) ->
                    if seenName(name)(seen)
                    then freeTypes(rest)(seen)(count)
                    else freeTypes(rest)(name :: seen)(count + 1)
                | _ -> freeTypes(rest)(seen)(count)

let items =
    [[Var(1)]
    |> Named(0)("Maybe")
    |> Fn(Var(1)), Named(0)("Int")([]), []
    |> Named(0)("Int")
    |> Fn(Var(2)), Named(0)("List")([Var(3)])]

let recursive repeat (times: Int) (total: Int) =
    if times == 0
    then total
    else repeat(times - 1)(total + freeTypes(items)([])(0))

0
|> repeat(3)
|> Ashes.IO.print
