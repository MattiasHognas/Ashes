// expect: b3 e3 h3 x1|b2 e2 h2 x1|b1 e1 h1 x1|

let recursive lastOf list =
    match list with
        | last :: [] -> last
        | _ :: rest -> lastOf(rest)
        | [] -> ""

let recursive secondToLast list =
    match list with
        | first :: _ :: [] -> first
        | _ :: rest -> secondToLast(rest)
        | [] -> ""

let recursive headOfLastPair list =
    match list with
        | (name, _) :: [] -> name
        | _ :: rest -> headOfLastPair(rest)
        | [] -> ""

let recursive lastOfLast lists =
    match lists with
        | (_ :: last :: []) :: [] -> last
        | _ :: rest -> lastOfLast(rest)
        | [] -> ""

let recursive walk (queue: List(List(Str))) (seen: List(Str)) (n: Int) =
    match queue with
        | [] ->
            match seen with
                | first :: _ -> first
                | [] -> ""
        | path :: rest ->
            let current = lastOf(path)
            in
                walk(if n < 3
                then Ashes.Collection.List.append(rest)([["x", current]])
                else rest)(seen)(n + 1)

let recursive rounds (n: Int) (acc: Str) =
    if n == 0
    then acc
    else
        let suffix = Ashes.Text.fromInt(n)
        in
            let path = ["a", "b" + suffix]
            in
                let a = lastOf(path)
                in
                    let e = headOfLastPair([("d", 1), ("e" + suffix, 2)])
                    in
                        let h = lastOfLast([["f"], ["g", "h" + suffix]])
                        in
                            let q = walk([["x" + Ashes.Text.fromInt(1)], ["c"], [secondToLast(["i", "j" + suffix, "k"])]])(["x1"])(0)
                            in rounds(n - 1)(acc + a + " " + e + " " + h + " " + q + "|")

""
|> rounds(3)
|> Ashes.IO.print
