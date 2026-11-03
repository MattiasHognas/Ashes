// expect: 31
type Tree(K, V) =
    | Leaf
    | Node(Tree(K, V), K, V)

let recursive insert key value tree =
    match tree with
        | Leaf -> Node(Leaf)(key)(value)
        | Node(left, k, v) ->
            Node(insert(key)(value)(left))(k)(v)

let recursive keys tree =
    match tree with
        | Leaf -> ""
        | Node(left, k, _) -> k + keys(left)

let recursive loop (items: List(Str)) seen (n: Int) =
    match items with
        | [] ->
            n + Ashes.Text.length(keys(seen))
        | name :: rest ->
            loop(rest)(seen
            |> insert(name)(n)
            |> insert(name + "!")(n))(n + 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + loop(["coal", "ash", "ember"])(Leaf)(1))

0
|> rounds(1)
|> Ashes.IO.print
