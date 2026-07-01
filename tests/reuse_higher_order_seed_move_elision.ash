// expect: 15 23 3 7
// CO-2 higher-order seed copy elision + result-freshness summary — soundness discriminator.
//
// A "higher-order seed" is a reuse-fold accumulator produced by a FUNCTION RESULT rather than a
// syntactic constructor tree (which is all `IsFullyFreshConstruction` can see through). `build` is a
// recursive builder proven *result-fresh*: on every path it returns a freshly-allocated tree — a
// `Leaf`, or a `Node` whose recursive `Tree` fields hold the result-fresh `build(n - 1)`/`Leaf` and
// whose only embedded parameter, `n`, sits in the copy-typed `Int` field (which cannot alias a cell
// the fold overwrites). So the whole-program move analysis may treat `build(_)`'s result as a move.
//
//   * `nested`  — `outer` is seeded by the DIRECT call `build(3)` (a result-fresh higher-order seed),
//     so `outer`'s accumulator is move-safe and the entry deep-copy of its inner direct-reuse fold
//     `grow` is ELIDED. root = 3 + 4 batches × (+3 per grow(3)) = 15.
//   * `viaLet`  — `accum` is seeded by a LET-BOUND result-fresh call (`let s = build(3) in ...`),
//     `s` used once (move-linear). Elided. root = 3 + 2 × (+10) = 23.
//   * `keep`    — `bump` is seeded by a let-bound result-fresh call `s2` that is ALSO RETAINED
//     (`keep = s2`, read afterwards), so the argument is NOT a move: the analysis DECLINES to elide,
//     `bump` overwrites its own copy, and `keep` still reads 3 (not the corrupted 203).
//   * `keepP`   — `dbump` is seeded by `pick(shared)`, and `pick t = t` is the identity (returns a
//     bare parameter), so it is NOT result-fresh: `pick(shared)` is not a fresh-result move and the
//     copy is KEPT. `keepP` (aliasing `shared`) still reads 7 (not the corrupted 57) — this guards
//     the result-fresh gate itself against wrongly admitting an aliasing (identity/wrap) builder.
type Tree =
    | Leaf
    | Node(Tree, Int, Tree)

let rootVal t = 
    match t with
        | Leaf -> -1
        | Node(l, v, r) -> v

let rec build n = 
    if n <= 0
    then Leaf
    else Node(build(n - 1))(n)(Leaf)

let pick t = t

let rec grow n t = 
    if n <= 0
    then t
    else 
        match t with
            | Leaf -> grow(n - 1)(Node(Leaf)(1)(Leaf))
            | Node(l, v, r) -> grow(n - 1)(Node(l)(v + 1)(r))

let rec accum n t = 
    if n <= 0
    then t
    else 
        match t with
            | Leaf -> accum(n - 1)(Node(Leaf)(10)(Leaf))
            | Node(l, v, r) -> accum(n - 1)(Node(l)(v + 10)(r))

let rec bump n t = 
    if n <= 0
    then t
    else 
        match t with
            | Leaf -> bump(n - 1)(Node(Leaf)(100)(Leaf))
            | Node(l, v, r) -> bump(n - 1)(Node(l)(v + 100)(r))

let rec dbump n t = 
    if n <= 0
    then t
    else 
        match t with
            | Leaf -> dbump(n - 1)(Node(Leaf)(50)(Leaf))
            | Node(l, v, r) -> dbump(n - 1)(Node(l)(v + 50)(r))

let rec outer b nb t = 
    if b >= nb
    then t
    else outer(b + 1)(nb)(grow(3)(t))

let nested = outer(0)(4)(build(3))

let s = build(3)

let viaLet = accum(2)(s)

let s2 = build(3)

let keep = s2

let bumped = bump(2)(s2)

let shared = build(7)

let keepP = shared

let pbumped = dbump(1)(pick(shared))
in Ashes.IO.print(Ashes.Text.fromInt(rootVal(nested)) + " " + Ashes.Text.fromInt(rootVal(viaLet)) + " " + Ashes.Text.fromInt(rootVal(keep)) + " " + Ashes.Text.fromInt(rootVal(keepP)))
