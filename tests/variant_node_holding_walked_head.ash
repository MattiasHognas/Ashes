// A loop grows a chain of variant nodes, each holding a head it walked out of a list of strings.
// The chain is normalized at the back edge after the walked cell is released: the arena node
// retains its head until the copy takes its own reference, and releases it right after.
// expect: 33
type Tree =
    | Leaf
    | Node(Str, Tree)

let recursive size map =
    match map with
        | Leaf -> 0
        | Node(_key, right) -> 1 + size(right)

let recursive grow (keys: List(Str)) (queued: Tree) =
    match keys with
        | [] -> queued
        | key :: rest ->
            queued
            |> Node(key)
            |> grow(rest)

let run (seed: Int) =
    size(grow(["cinder" + Ashes.Text.fromInt(seed), "ember" + Ashes.Text.fromInt(seed), "smoke"])(Leaf)) + seed

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(8))

0
|> rounds(3)
|> Ashes.IO.print
