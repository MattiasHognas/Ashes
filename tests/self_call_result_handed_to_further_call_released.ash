// expect: 28
type Export =
    | ValueExport(Str)
    | TypeExport(Str)

let exportsFor (item: Int) =
    if item > 2
    then [ValueExport("v" + Ashes.Text.fromInt(item))]
    else [TypeExport("t" + Ashes.Text.fromInt(item)), ValueExport("c")]

let recursive appendExports left right =
    match left with
        | [] -> right
        | head :: rest -> head :: appendExports(rest)(right)

let recursive collectExports items =
    match items with
        | [] -> []
        | item :: rest ->
            rest
            |> collectExports
            |> appendExports(exportsFor(item))

let recursive count (xs: List(Export)) =
    match xs with
        | [] -> 0
        | ValueExport(_) :: rest -> 1 + count(rest)
        | TypeExport(_) :: rest -> 2 + count(rest)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        loop(n - 1)(acc + count(collectExports([1, 2, 3, n])))

0
|> loop(3)
|> Ashes.IO.print
