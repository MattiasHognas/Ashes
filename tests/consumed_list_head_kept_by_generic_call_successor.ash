// expect: a=1,b=1,c=1
let recursive setAssociation key value entries =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setAssociation(key)(value)(tail)

let recursive addCases (cs: List(Str)) entries =
    match cs with
        | [] -> entries
        | label :: cTail ->
            entries
            |> setAssociation(label)(1)
            |> addCases(cTail)

let recursive render entries =
    match entries with
        | [] -> []
        | (k, v) :: rest -> k + "=" + Ashes.Text.fromInt(v) :: render(rest)

[]
|> addCases(["a", "b" + "", "a", "c"])
|> render
|> Ashes.Text.join(",")
|> Ashes.IO.print
