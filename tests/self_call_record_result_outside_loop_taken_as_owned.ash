// expect: 4|abcd
import Ashes.Text
import Ashes.IO
// A non-tail recursion that rebuilds a tree around its own result: the self call's result is an ADT
// (a general reference-counted value) the caller owns through the returns bit and stores in the
// node it builds, with no back edge to release it at.
type Seen =
    | Leaf
    | Node(Seen, Str, Seen)

let recursive insert (key: Str) (seen: Seen) =
    match seen with
        | Leaf -> Node(Leaf)(key)(Leaf)
        | Node(left, current, right) ->
            if key < current
            then
                Node(insert(key)(left))(current)(right)
            else
                right
                |> insert(key)
                |> Node(left)(current)

let recursive count (seen: Seen) =
    match seen with
        | Leaf -> 0
        | Node(left, _current, right) -> count(left) + 1 + count(right)

let recursive keys (seen: Seen) =
    match seen with
        | Leaf -> ""
        | Node(left, current, right) -> keys(left) + current + keys(right)

let tree =
    Leaf
    |> insert("c")
    |> insert("a")
    |> insert("d")
    |> insert("b")
in
    Ashes.IO.print(Ashes.Text.fromInt(count(tree)) + "|" + keys(tree))
