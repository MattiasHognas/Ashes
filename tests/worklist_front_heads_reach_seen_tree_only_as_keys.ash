// A worklist whose front queue is walked and refilled from its reversed back queue while each
// walked head is marked in a tree of seen keys. The tree receives only the front's string heads,
// which it retains, never the front's spine, so it does not keep the front in the arena: the
// front lives on the reference-counted heap and each refill is released once walked.
// expect: 45
type Tree(K, V) =
    | Leaf
    | Node(Tree(K, V), K, V, Tree(K, V))

let compareKeys (left: Str) (right: Str) =
    right
    |> Ashes.Byte.fromText
    |> Ashes.Byte.compare(Ashes.Byte.fromText(left))

let setStr newKey newValue =
    (let recursive go map =
        match map with
            | Leaf -> Node(Leaf)(newKey)(newValue)(Leaf)
            | Node(left, key, value, right) ->
                let ordering = compareKeys(newKey)(key)
                in
                    if ordering == 0
                    then Node(left)(key)(newValue)(right)
                    else
                        if ordering <= -1
                        then
                            Node(go(left))(key)(value)(right)
                        else
                            right
                            |> go
                            |> Node(left)(key)(value)
    in go)

let recursive size map =
    match map with
        | Leaf -> 0
        | Node(left, _key, _value, right) -> size(left) + 1 + size(right)

let recursive reverseOnto (acc: List(Str)) (items: List(Str)) =
    match items with
        | [] -> acc
        | head :: tail -> reverseOnto(head :: acc)(tail)

let recursive solve (front: List(Str)) (back: List(Str)) (queued: Tree(Str, Bool)) (fuel: Int) =
    match front with
        | [] ->
            match back with
                | [] -> queued
                | _ ->
                    solve(reverseOnto([])(back))([])(queued)(fuel)
        | key :: rest ->
            if fuel <= 0
            then queued
            else
                solve(rest)(key + "x" :: back)(setStr(key)(true)(queued))(fuel - 1)

let run (seed: Int) =
    size(solve(["cinder", "ember"])([])(Leaf)(7)) + seed

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(8))

0
|> rounds(3)
|> Ashes.IO.print
