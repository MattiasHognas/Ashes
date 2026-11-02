// expect: 141 70
capability Cap =
    | get : Unit -> Str

let recursive gather (i: Int) (acc: Str) =
    if i == 0
    then acc
    else gather(i - 1)(acc + perform Cap.get(Unit))

let recursive collect (i: Int) (acc: List(Str)) =
    if i == 0
    then acc
    else collect(i - 1)(perform Cap.get(Unit) :: acc)

let text (n: Int) =
    Ashes.Text.byteLength(handle n
    |> Ashes.Text.fromInt
    |> gather(3) with
        | Cap.get(u) ->
            let x = resume("lit")
            in x + "s"
        | return(r) -> r + "r")

let items (n: Int) =
    Ashes.Collection.List.length(handle collect(3)([Ashes.Text.fromInt(n)]) with
        | Cap.get(u) ->
            let x = resume("lit")
            in "s" :: x
        | return(r) -> r)

let recursive loop (n: Int) (texts: Int) (lists: Int) =
    if n == 0
    then Ashes.Text.fromInt(texts) + " " + Ashes.Text.fromInt(lists)
    else loop(n - 1)(texts + text(n))(lists + items(n))
in
    0
    |> loop(10)(0)
    |> Ashes.IO.print
