// expect: x/1!,x/0!,b/c!,a!|1/z
let recursive walk (queue: List(List(Str))) (seen: List(Str)) (n: Int) =
    match queue with
        | [] -> Ashes.Text.join(",")(seen)
        | path :: rest ->
            let current = Ashes.Text.join("/")(path)
            in
                if n > 100
                then walk(rest)(seen)(n + 1)
                else
                    walk(Ashes.Collection.List.append(rest)(if n < 2
                    then [["x", Ashes.Text.fromInt(n)]]
                    else []))(current + "!" :: seen)(n + 1)

let recursive firstLong (queue: List(List(Str))) (n: Int) =
    match queue with
        | [] -> ["none"]
        | path :: rest ->
            if n == 4
            then path
            else
                if n > 2
                then firstLong(rest)(n + 1)
                else
                    firstLong(Ashes.Collection.List.append(rest)([[Ashes.Text.fromInt(n), "z"]]))(n + 1)

Ashes.IO.print(walk([["a"], ["b", "c"]])([])(0) + "|" + Ashes.Text.join("/")(firstLong([["p"], ["q"], ["r"]])(0)))
