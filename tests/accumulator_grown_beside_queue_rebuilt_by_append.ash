// expect: 24

let recursive walk (queue: List(List(Str))) (seen: List(Str)) (n: Int) =
    match queue with
        | [] -> n + Ashes.Collection.List.length(seen)
        | path :: rest ->
            let current = Ashes.Text.join("")(path)
            in
                if n > 100
                then walk(rest)(seen)(n + 1)
                else
                    walk(Ashes.Collection.List.append(rest)(if n < 2
                    then [["x"]]
                    else []))(current :: seen)(n + 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + walk([["a"], ["b" + Ashes.Text.fromInt(n)]])([])(0))

0
|> rounds(3)
|> Ashes.IO.print
