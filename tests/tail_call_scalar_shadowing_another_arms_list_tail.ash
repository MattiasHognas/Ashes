// expect: 7
let recursive hop (xs: List(Str)) (acc: Int) =
    match xs with
        | [] -> acc
        | "zero" :: rest -> hop(rest)(acc)
        | _ :: next ->
            let rest = 7
            in hop(next)(rest)

0
|> hop(["zero", "one"])
|> Ashes.IO.print
