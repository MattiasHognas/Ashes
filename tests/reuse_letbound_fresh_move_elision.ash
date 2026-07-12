// expect: 12 1 201
// CO-2 richer-aliasing increment — a let-bound (not call-site-syntactic) fresh seed is a move.
//
// `grow` is a DIRECT-reuse fold; `outer` threads its accumulator into `grow` once per batch. Unlike
// tests/reuse_direct_move_elision.ash (which seeds `outer` with an INLINE fresh construction at the
// call site), here `outer` is seeded through `seed`, a `let`-bound name. The seed rule that demands
// syntactic freshness AT the call site does not fire for a bare `Var`. The richer-aliasing rule does:
// `seed` is bound (locally, on the top-level spine) to the FULLY-FRESH construction `Node(Leaf)(0)(Leaf)`
// (no variable reference anywhere) and is move-linear — used exactly once, dead afterwards, never
// captured. So it is uniquely owned and moved, and `grow`'s entry deep-copy is ELIDED.
//
// Discriminator: `shared` is ALSO `let`-bound to a fully-fresh construction, but it is RETAINED —
// referenced by both `keep` and `bump`, so it is NOT move-linear (two occurrences). The analysis
// therefore DECLINES to elide `bump`'s copy — the argument is not a move — so `bump`'s in-place
// update rewrites its own copy, leaving `shared` (aliased by `keep`) intact: `keep` still reads 1,
// not the corrupted 201. A wrongly-enabled elision (ignoring the move-linear guard) would corrupt it.
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
            | Leaf -> grow(n - 1)(Node(Leaf)(1)(Leaf))
            | Node(l, v, r) -> grow(n - 1)(Node(l)(v + 1)(r))

let recursive outer b nb t =
    if b >= nb
    then t
    else outer(b + 1)(nb)(grow(3)(t))

let seed = Node(Leaf)(0)(Leaf)

let nested = outer(0)(4)(seed)

let recursive bump n t =
    if n <= 0
    then t
    else
        match t with
            | Leaf -> bump(n - 1)(Node(Leaf)(1)(Leaf))
            | Node(l, v, r) -> bump(n - 1)(Node(l)(v + 100)(r))

let shared = Node(Leaf)(1)(Leaf)

let keep = shared

let bumped = bump(2)(shared)
in Ashes.IO.print(Ashes.Text.fromInt(rootVal(nested)) + " " + Ashes.Text.fromInt(rootVal(keep)) + " " + Ashes.Text.fromInt(rootVal(bumped)))
