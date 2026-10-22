// expect: 24
type Names =
    | Names(List(Str), Int)
    | NoNames

let add (name: Str) (acc: Names) =
    match acc with
        | Names(names, count) -> Names(name :: names)(count + 1)
        | NoNames -> Names([name])(1)

let size (acc: Names) =
    match acc with
        | Names(_names, count) -> count
        | NoNames -> 0

let recursive walk (items: List(Str)) (acc: Names) =
    match items with
        | [] -> size(acc)
        | item :: rest ->
            acc
            |> add(item)
            |> walk(rest)

let items = ["a", "b", "c", "d", "e", "f", "g", "h"]

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + walk(items)(NoNames))

0
|> rounds(3)
|> Ashes.IO.print
