// expect: 10
// A tree threaded through a loop by a call to an in-place specialization candidate, the successor bound
// by a `let`: the tree is decided by its type, not by the list shape of its self-call, and each
// iteration rewrites or replaces the version before it.
type Tree =
    | Leaf
    | Node(Tree, Str, Tree)

let insert (k: Str) =
    (let recursive go s =
        match s with
            | Leaf -> Node(Leaf)(k)(Leaf)
            | Node(l, x, r) ->
                if k < x
                then
                    Node(go(l))(x)(r)
                else
                    if x < k
                    then
                        r
                        |> go
                        |> Node(l)(x)
                    else Node(l)(k)(r)
    in go)

let recursive size t =
    match t with
        | Leaf -> 0
        | Node(l, _x, r) -> size(l) + 1 + size(r)

let recursive build i n m =
    if i >= n
    then m
    else
        let m2 =
            insert(Ashes.Text.fromInt(i - i / 10 * 10))(m)
        in build(i + 1)(n)(m2)

let built = build(0)(100)(Leaf)
in
    built
    |> size
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
