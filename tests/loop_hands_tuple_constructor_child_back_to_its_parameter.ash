// expect: 506500
type Kept =
    | state: List(Int)
    | temp: Int

type Tree =
    | Leaf(Kept)
    | Node(Tree, Tree)

let recursive build (d: Int) (n: Int) =
    if d == 0
    then Leaf(Kept(state = [n, d], temp = n))
    else
        n + d
        |> build(d - 1)
        |> Node(build(d - 1)(n))

let recursive leftmost (t: Tree) (acc: Int) =
    match (t, acc) with
        | (Leaf(Kept { state = st, temp = k }), a) -> a + k + Ashes.Collection.List.length(st)
        | (Node(l, _), a) -> leftmost(l)(a + 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + leftmost(build(4)(n))(0))

0
|> rounds(1000)
|> Ashes.IO.print
