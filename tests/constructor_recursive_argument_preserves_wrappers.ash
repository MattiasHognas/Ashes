// expect: 7
type Tree =
    | Leaf(Int)
    | Wrap(Tree)
    | Pair(Tree, Tree)

let recursive preserve tree remaining =
    match tree with
        | Wrap(inner) ->
            remaining
            |> preserve(inner)
            |> Wrap
        | Pair(left, right) ->
            remaining
            |> preserve(right)
            |> Pair(preserve(left)(remaining))
        | Leaf(_value) ->
            if remaining == 0
            then tree
            else preserve(tree)(remaining - 1)

let recursive wrappers tree =
    match tree with
        | Leaf(_value) -> 0
        | Wrap(inner) -> 1 + wrappers(inner)
        | Pair(left, right) -> wrappers(left) + wrappers(right)

type Node(a) =
    | End(a)
    | Next(Node(a))

let recursive preserveNode node remaining =
    match node with
        | Next(inner) ->
            remaining
            |> preserveNode(inner)
            |> Next
        | End(_value) ->
            if remaining == 0
            then node
            else preserveNode(node)(remaining - 1)

let recursive nodeDepth node =
    match node with
        | End(_value) -> 0
        | Next(inner) -> 1 + nodeDepth(inner)

let unary = Wrap(Wrap(Leaf(7)))

let branching = Pair(Wrap(Wrap(Leaf(8))))(Wrap(Leaf(9)))

wrappers(preserve(unary)(2)) + wrappers(preserve(branching)(2)) + nodeDepth(preserveNode(Next(Next(End(7))))(2))
|> Ashes.Text.fromInt
|> Ashes.IO.print
