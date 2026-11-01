// expect: 6000

type Layout =
    | layoutName: Maybe(Str)
    | layoutArity: Int

let layoutOf (n: Int) =
    if n > 5
    then Some(Layout(layoutName = Some("big"), layoutArity = n))
    else None

let keyOf (n: Int) =
    match layoutOf(n) with
        | Some(layout) -> layout.layoutName
        | None -> None

let recursive countKeys (candidates: List(Int)) (groupKey: Int -> Maybe(Str)) (total: Int) =
    match candidates with
        | [] -> total
        | arm :: rest ->
            match groupKey(arm) with
                | Some(key) -> countKeys(rest)(groupKey)(total + Ashes.Text.length(key))
                | None -> countKeys(rest)(groupKey)(total)

let run (seed: Int) = countKeys([seed, seed + 7, seed + 2])(keyOf)(0)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(1))

0
|> rounds(2000)
|> Ashes.IO.print
