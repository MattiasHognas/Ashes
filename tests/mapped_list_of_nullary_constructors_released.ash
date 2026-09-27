// expect: 21 6 12

type Kind =
    | Alpha
    | Beta
    | Gamma

let kindOf (n: Int) =
    if n % 3 == 0
    then Alpha
    else
        if n % 3 == 1
        then Beta
        else Gamma

let describe (kinds: List(Kind)) =
    match kinds with
        | Alpha :: _ -> "alpha first"
        | Beta :: Alpha :: _ -> "beta then alpha"
        | [] -> "none"
        | _ -> "other"

let recursive countAlpha (kinds: List(Kind)) (total: Int) =
    match kinds with
        | [] -> total
        | Alpha :: rest -> countAlpha(rest)(total + 1)
        | _ :: rest -> countAlpha(rest)(total)

let recursive walk (nums: List(Int)) (total: Int) =
    match nums with
        | [] -> total
        | _ :: rest ->
            match Ashes.Collection.List.map(kindOf)(nums) with
                | Alpha :: _ -> walk(rest)(total + 1)
                | Beta :: _ -> walk(rest)(total + 2)
                | _ -> walk(rest)(total)

let recursive rounds (n: Int) (described: Int) (counted: Int) (walked: Int) =
    if n == 0
    then Ashes.Text.fromInt(described) + " " + Ashes.Text.fromInt(counted) + " " + Ashes.Text.fromInt(walked)
    else
        let kinds = Ashes.Collection.List.map(kindOf)([n, n + 1, n + 2])
        in
            let more = Ashes.Collection.List.map(kindOf)([n, n + 1, n + 2, n + 3, n + 4, n + 5])
            in
                rounds(n - 1)(described + Ashes.Text.byteLength(describe(kinds)))(counted + countAlpha(more)(0))(walked + walk([n, n + 1, n + 2, n + 3])(0))

0
|> rounds(3)(0)(0)
|> Ashes.IO.print
