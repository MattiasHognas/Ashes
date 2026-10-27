// expect: 300
let recursive rep (path: Str) =
    match Ashes.Text.unconsText(path) with
        | None -> ""
        | Some((head, tail)) -> head + rep(tail)

let parts (flag: Bool) (path: Str) =
    (let slashed = rep(path)
    in
        if flag
        then ("", true, Ashes.Text.split(slashed)("/"))
        else
            match Ashes.Text.split(slashed)("/") with
                | server :: rest -> (server + "x", true, [])
                | _ -> ("", true, []))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + (match parts(true)("/a/./b/../c" + Ashes.Text.fromInt(n)) with
            | (p, a, ps) -> Ashes.Text.length(p) + Ashes.Collection.List.length(ps)))

0
|> rounds(50)
|> Ashes.IO.print
