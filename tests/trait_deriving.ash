// expect: Branch(Leaf(1), Leaf(2))|Point(x = 4, y = 5)|true|true|844465531482723
import Ashes.Text
type Tree(a) =
    | Leaf(a)
    | Branch(Tree(a), Tree(a))
    deriving {Eq, Ord, Show, Hash}

type Point =
    | x: Int
    | y: Int
    deriving {Eq, Ord, Show, Hash}

let first = Branch(Leaf(1))(Leaf(2))

let second = Branch(Leaf(1))(Leaf(3))

let point = Point(x = 4, y = 5)

let equal =
    if first == first
    then "true"
    else "false"

let ordered =
    if first < second
    then "true"
    else "false"

Ashes.IO.print(Ashes.Trait.Show.show(first) + "|" + Ashes.Trait.Show.show(point) + "|" + equal + "|" + ordered + "|" + Ashes.Text.fromInt(Ashes.Trait.Hash.hash(first)))
