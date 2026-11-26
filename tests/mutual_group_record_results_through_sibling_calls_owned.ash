// expect: 6|7|abc
import Ashes.Text
import Ashes.IO
// A mutual recursion whose members return a record and an ADT through calls to each other, none of
// them in a loop body: the sibling's result type is closed by the type checker, so each call takes it
// owned through the contract instead of asking the arena, and the caller releases what it does not
// keep.
type Tally =
    | label: Str
    | count: Int

type Tree =
    | Leaf(Str)
    | Branch(Tree, Tree)

let recursive countTree (tree: Tree) =
    match tree with
        | Leaf(text) -> Tally(label = text, count = 1)
        | Branch(left, right) -> combine(left)(right)
and combine (left: Tree) (right: Tree) =
    match (countTree(left), countTree(right)) with
        | (Tally { label = leftLabel, count = leftCount }, Tally { label = rightLabel, count = rightCount }) -> Tally(label = leftLabel + rightLabel, count = leftCount + rightCount + 1)

let recursive mirror (tree: Tree) =
    match tree with
        | Leaf(text) -> Leaf(text)
        | Branch(left, right) -> swap(left)(right)
and swap (left: Tree) (right: Tree) =
    left
    |> mirror
    |> Branch(mirror(right))

let recursive size (tree: Tree) =
    match tree with
        | Leaf(_text) -> 1
        | Branch(left, right) -> size(left) + size(right) + 1

let tree =
    Leaf("c")
    |> Branch(Leaf("b"))
    |> Branch(Leaf("a"))

let tally = countTree(tree)
in
    Ashes.IO.print(Ashes.Text.fromInt(tally.count + 1) + "|" + Ashes.Text.fromInt(size(mirror(tree)) + 2) + "|" + tally.label)
