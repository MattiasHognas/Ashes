// expect: a,b,c

let recursive join values =
    match values with
        | [] -> ""
        | value :: [] -> value
        | value :: tail -> value + "," + join(tail)

let recursive rebuild values preserve =
    match values with
        | [] -> []
        | head :: tail ->
            if preserve
            then
                true
                |> rebuild(tail)
                |> (given (rebuilt) -> head :: rebuilt)
            else rebuild(tail)(true)

true
|> rebuild(["a", "b", "c"])
|> join
|> Ashes.IO.print
