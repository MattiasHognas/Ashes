// expect: 200
// A tree walk threading a list accumulator through a non-tail recursion keeps its generic body: a
// copy at the accumulator's type would keep the recursion, so the fold specialization declines it.
type Tree =
    | Leaf
    | Branch(Tree, Int, Tree)

let recursive grow n =
    match n with
        | 0 -> Leaf
        | _ ->
            (n - 1) / 2
            |> grow
            |> Branch(grow(n / 2))(n)

let foldTree folder state =
    (let recursive go acc tree =
        match tree with
            | Leaf -> acc
            | Branch(left, key, right) ->
                let afterLeft = go(acc)(left)
                in
                    let afterNode = folder(afterLeft)(key)
                    in go(afterNode)(right)
    in go(state))

let step (acc: List(Int)) (key: Int) = key :: acc

let final =
    200
    |> grow
    |> foldTree(step)([])

final
|> Ashes.Collection.List.length
|> Ashes.Text.fromInt
|> Ashes.IO.print
