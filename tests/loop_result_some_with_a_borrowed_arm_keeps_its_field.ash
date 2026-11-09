// expect: 140
let recursive firstOrLabel (hops: Int) (label: Str) (names: List(Str)) =
    if hops <= 0
    then Some(label)
    else
        match names with
            | [] -> None
            | name :: rest ->
                if Ashes.Text.length(name) > 7
                then Some(name)
                else firstOrLabel(hops - 1)(label)(rest)

let measure (names: List(Str)) (start: Str) (hops: Int) =
    match firstOrLabel(hops)(start)(names) with
        | Some(found) -> Ashes.Text.length(found)
        | None -> 0

let recursive loop (n: Int) (names: List(Str)) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(names)(acc + measure(names)("label_" + Ashes.Text.fromInt(n % 3))(n % 3))

0
|> loop(20)(["a", "bb", "lambda_123"])
|> Ashes.IO.print
