// expect: 15 7 207
// Result-alias copy elision + may-alias reach summary — soundness discriminator.
//
// A `wrap`-style builder EMBEDS its parameter into a heap field of a fresh cell:
// `wrap v x = Node(x)(v)(Leaf)` — its result may ALIAS parameter `x` (the may-alias reach summary
// records reach {x}; the `Int` field `v` is copy-typed and carries no heap reachability). This is
// exactly the case `IsFullyFreshConstruction` (no variables) and the earlier result-freshness rule
// (bare-param heap field ⇒ not fresh) both decline. The reach summary instead admits `wrap(...)` as
// a move iff the argument bound to `x` is itself a move.
//
//   * `nested` — `outer`'s accumulator is seeded by `wrap(3)(Node(Leaf)(5)(Leaf))`: the reached
//     parameter `x` is bound to a FULLY-FRESH construction (a move), so `wrap(...)` is a move and the
//     entry deep-copy of the inner direct-reuse fold `grow` is ELIDED. root = 3 + 4 batches × (+3) = 15.
//   * `keep`   — `bump` is seeded by a let-bound `wrap(7)(...)` result `w` that is ALSO RETAINED
//     (`keep = w`, read afterwards), so `w` is not move-linear: the analysis DECLINES to elide,
//     `bump` overwrites its own copy, and `keep` still reads 7 (not the corrupted 207) — the
//     soundness discriminator.
//   * `bumped` — `bump(2)(w)` over the kept copy: 7 + 2 × (+100) = 207.
type Tree =
    | Leaf
    | Node(Tree, Int, Tree)

let rootVal t =
    match t with
        | Leaf -> -1
        | Node(l, v, r) -> v

let wrap v x = Node(x)(v)(Leaf)

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
    |> Node(Leaf)(5)
    |> wrap(3)
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

let w =
    Leaf
    |> Node(Leaf)(1)
    |> wrap(7)

let keep = w

let bumped = bump(2)(w)
in
    Ashes.IO.print(Ashes.Text.fromInt(rootVal(nested)) + " " + Ashes.Text.fromInt(rootVal(keep)) + " " + Ashes.Text.fromInt(rootVal(bumped)))
