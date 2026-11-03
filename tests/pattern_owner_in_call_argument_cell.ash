// expect: 200
type Item =
    | ItemPlain(Str)

type Tree =
    | Leaf
    | Node(Tree, Item, Tree)

let recursive insert (value: Item) (tree: Tree) =
    match tree with
        | Leaf -> Node(Leaf)(value)(Leaf)
        | Node(left, v, right) ->
            Node(insert(value)(left))(v)(right)

let recursive loop (items: List(Str)) (seen: Tree) (count: Int) =
    match items with
        | [] -> count
        | name :: rest ->
            loop(rest)(insert(ItemPlain(name))(seen))(count + 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + loop(["coal", "ash", "dust"])(Leaf)(1))

0
|> rounds(50)
|> Ashes.IO.print
