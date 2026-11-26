// expect: 28
// A curried two-parameter producer appends a typed list: each matched head is consed onto the
// append of the rest, in an arena cell the join copies onto the reference-counted heap. The
// reference the cell took of its pattern-bound head goes to the slot the cons registered, written
// behind the join's copy, so no head outlives the copy that holds it.
type Export =
    | ValueExport(Str)
    | TypeExport(Str)

let exportsFor (item: Int) =
    if item > 2
    then [ValueExport("v" + Ashes.Text.fromInt(item))]
    else [TypeExport("t" + Ashes.Text.fromInt(item)), ValueExport("c")]

let recursive appendExports (left: List(Export)) (right: List(Export)) =
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
