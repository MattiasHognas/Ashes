// expect: 200
// A non-tail tree walk threads a list accumulator: each node conses onto the list the left walk
// returned. The arena cell carrying that list to the next iteration keeps the list alive through a
// reference held in an owned slot; each iteration moves the reference the previous one stored to a
// predecessor slot the back edge releases, instead of overwriting it, so the walk keeps one list
// rather than one per node.
type Tree =
    | Leaf
    | Branch(Tree, Int, List(Int), Tree)

let recursive grow n =
    match n with
        | 0 -> Leaf
        | _ ->
            (n - 1) / 2
            |> grow
            |> Branch(grow(n / 2))(n)([n % 7, n % 5])

let recursive copyInto (acc: List(List(Int))) (tree: Tree) =
    match tree with
        | Leaf -> acc
        | Branch(left, key, value, right) ->
            let afterLeft = copyInto(acc)(left)
            in
                let afterNode = (key :: value) :: afterLeft
                in copyInto(afterNode)(right)

let final =
    200
    |> grow
    |> copyInto([])

final
|> Ashes.Collection.List.length
|> Ashes.Text.fromInt
|> Ashes.IO.print
