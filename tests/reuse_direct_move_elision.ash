// expect: 12 1 201
// Direct-reuse copy elision + fresh-construction move seed — soundness discriminator.
//
// `grow` is a DIRECT-reuse fold: its own loop body matches the recursive-ADT accumulator `t` and
// rebuilds it with the same `Node` constructor in place. `outer` threads its accumulator `t` into
// `grow` once per batch. The whole-program move analysis proves `grow`'s accumulator is uniquely
// owned at every external call site: `outer` passes its own move-safe `t` (used once per path), and
// `outer` itself is seeded with the syntactically FULLY-FRESH construction `Node(Leaf)(0)(Leaf)`
// (no variable reference anywhere — unaliased by construction). So `grow`'s entry deep-copy is
// ELIDED; the nested re-entry (`outer` calling `grow` per batch) no longer re-copies per batch.
//
// `bump` threads the SAME direct-reuse shape, but its only external call site passes `base`, which
// is ALSO retained (`keep = base`, read afterwards). The analysis therefore DECLINES to elide
// `bump`'s copy — the argument is not a move — so `bump`'s in-place update rewrites its own copy,
// leaving `base` (aliased by `keep`) intact: `keep` still reads 1, not the corrupted 201.
type Tree =
    | Leaf
    | Node(Tree, Int, Tree)

let rootVal t =
    match t with
        | Leaf -> -1
        | Node(l, v, r) -> v

let recursive grow n t =
    if n <= 0
    then t
    else
        match t with
            | Leaf ->
                Leaf
                |> Node(Leaf)(1)
                |> grow(n - 1)
            | Node(l, v, r) ->
                r
                |> Node(l)(v + 1)
                |> grow(n - 1)

let recursive outer b nb t =
    if b >= nb
    then t
    else
        t
        |> grow(3)
        |> outer(b + 1)(nb)

let nested =
    Leaf
    |> Node(Leaf)(0)
    |> outer(0)(4)

let recursive bump n t =
    if n <= 0
    then t
    else
        match t with
            | Leaf ->
                Leaf
                |> Node(Leaf)(1)
                |> bump(n - 1)
            | Node(l, v, r) ->
                r
                |> Node(l)(v + 100)
                |> bump(n - 1)

let base = bump(1)(Leaf)

let keep = base

let bumped = bump(2)(base)
in
    Ashes.IO.print(Ashes.Text.fromInt(rootVal(nested)) + " " + Ashes.Text.fromInt(rootVal(keep)) + " " + Ashes.Text.fromInt(rootVal(bumped)))
