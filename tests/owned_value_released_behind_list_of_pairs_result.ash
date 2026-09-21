// A function that hands a fresh list of owned trees to a callee and returns the callee's table of
// string pairs: the table is made independent first, and the owned list is released behind it. A
// table of pairs has no fixed head copy, so it used to keep the owned list alive forever.
// expect: 1875
type Tree =
    | Leaf(Str)
    | Node(Tree, Tree)

let build (n: Int) =
    Leaf("x")
    |> Node(n + 1
    |> Ashes.Text.fromInt
    |> Leaf)
    |> Node(Leaf("f" + Ashes.Text.fromInt(n)))

let recursive trees (n: Int) (acc: List(Tree)) =
    if n == 0
    then acc
    else trees(n - 1)(build(n) :: acc)

let recursive addPairs (remaining: List(Tree)) (table: List((Str, Str))) =
    match remaining with
        | [] -> table
        | Node(Leaf(name), _) :: rest -> addPairs(rest)((name, name + "!") :: table)
        | _ :: rest -> addPairs(rest)(table)

let pairsOf (n: Int) =
    addPairs(trees(n - n / 4 * 4 + 1)([]))([])

let recursive weigh (pairs: List((Str, Str))) (total: Int) =
    match pairs with
        | [] -> total
        | (left, right) :: rest -> weigh(rest)(total + Ashes.Text.byteLength(left) + Ashes.Text.byteLength(right))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + weigh(pairsOf(n))(0))

0
|> rounds(150)
|> Ashes.IO.print
