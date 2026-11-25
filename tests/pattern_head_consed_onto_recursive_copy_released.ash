// expect: 10
// A typed list copied by a recursive producer: each matched head is consed onto the copy of the
// rest, in an arena cell the producer's result copies onto the reference-counted heap. The
// reference the cell took of its pattern-bound head is released behind the result, so no head
// outlives the copy that holds it.
type Export =
    | ValueExport(Str)
    | TypeExport(Str)

let recursive copyAll (left: List(Export)) =
    match left with
        | [] -> []
        | head :: rest -> head :: copyAll(rest)

let recursive count (xs: List(Export)) =
    match xs with
        | [] -> 0
        | _ :: rest -> 1 + count(rest)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        loop(n - 1)(acc + count(copyAll([ValueExport("a" + Ashes.Text.fromInt(n))])))

0
|> loop(10)
|> Ashes.IO.print
