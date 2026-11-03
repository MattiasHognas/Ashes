// expect: 250
type Item =
    | ItemPlain(Str)

type Tree =
    | Leaf
    | Node(Tree, Item)

let recursive loop (items: List(Str)) (seen: Tree) (count: Int) =
    match items with
        | [] -> count
        | name :: rest ->
            loop(rest)(Node(seen)(ItemPlain(name)))(count + 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + loop(["coal", "ash", "dust", "soot"])(Leaf)(1))

0
|> rounds(50)
|> Ashes.IO.print
