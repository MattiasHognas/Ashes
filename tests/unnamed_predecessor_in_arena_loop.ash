// expect: 250
type Item =
    | ItemPlain(Str)
    | ItemWrapped(Item)

type Tree(K, V) =
    | Leaf
    | Node(Tree(K, V), K, V, Tree(K, V))

let recursive insert (key: Str) (value: Item) (tree: Tree(Str, Item)) =
    match tree with
        | Leaf -> Node(Leaf)(key)(value)(Leaf)
        | Node(left, k, v, right) ->
            Node(insert(key)(value)(left))(k)(v)(right)

let recursive loop (items: List(Item)) (seen: Tree(Str, Item)) (count: Int) =
    match items with
        | [] -> count
        | ItemWrapped(inner) :: rest -> loop(inner :: rest)(seen)(count)
        | ItemPlain(name) :: rest ->
            loop(rest)(insert(name)(ItemPlain(name))(seen))(count + 1)

let run (seed: Int) = loop([ItemPlain("ash"), ItemPlain("dust"), ItemPlain("coal"), ItemPlain("soot")])(Leaf)(seed)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(1))

0
|> rounds(50)
|> Ashes.IO.print
