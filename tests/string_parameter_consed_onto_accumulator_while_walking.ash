// expect: 9|,;,;,|,;,;,|,;,;,

let recursive collect (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else collect(n - 1)(Ashes.Text.fromInt(n) :: acc)

let recursive stamp (separator: Str) (acc: List(Str)) (ps: List(Str)) =
    match ps with
        | [] -> acc
        | _ :: rest -> stamp(separator)(separator :: acc)(rest)

let recursive rounds (n: Int) (count: Int) (out: Str) =
    if n == 0
    then Ashes.Text.fromInt(count) + out
    else
        let stamped =
            []
            |> collect(3)
            |> stamp(",")([])
        in rounds(n - 1)(count + Ashes.Collection.List.length(stamped))(out + "|" + Ashes.Text.join(";")(stamped))

""
|> rounds(3)(0)
|> Ashes.IO.print
