// expect: 36
type Rec =
    | label: Str
    | positions: List(Int)

let make (n: Int) = Rec(label = "r" + Ashes.Text.fromInt(n), positions = [n, n + 1])

let recursive walk (items: List(Int)) (removed: List(List(Int))) (acc: Int) =
    match items with
        | [] -> (removed, acc)
        | n :: rest ->
            let record = make(n)
            in walk(rest)(record.positions :: removed)(acc + Ashes.Text.byteLength(record.label))

let recursive total (lists: List(List(Int))) =
    match lists with
        | [] -> 0
        | xs :: rest -> Ashes.Collection.List.length(xs) + total(rest)

let probe (n: Int) =
    match walk([n, 2, 3])([])(0) with
        | (removed, acc) -> acc + total(removed)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(3)
|> Ashes.IO.print
