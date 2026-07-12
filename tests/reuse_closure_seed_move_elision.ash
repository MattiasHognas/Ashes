// expect: 17 6 7 207
// CO-2d closure / function-valued seed copy elision + capture-aware result-reach — soundness discriminator.
//
// A reuse-fold seed produced by a CLOSURE the currying did not statically flatten: `makeFresh`,
// `makeCap` and `makeId` each return a lambda from behind an `if`, so they are 1-arg functions whose
// RESULT is a closure. Applying that closure — `(makeFresh(true))(5)` — is an OVER-APPLICATION the
// move analysis now sees through by inlining the callee one level and binding the surplus argument to
// the returned lambda's parameter (capture-aware: a captured parameter embedded in the produced value
// is reached via its argument, a captured global or unmodeled capture poisons = keep copy).
//
//   * `nested`  — `outer` is seeded by the no-capture closure `(makeFresh(true))(5)` (reach {}, a
//     result-fresh higher-order seed), so `outer`'s accumulator is move-safe and the entry deep-copy of
//     its inner direct-reuse fold `grow` is ELIDED. root = 5 + 4 batches × (+3 per grow(3)) = 17.
//   * `capMove` — `outer` is also seeded by `(makeCap(Node(Leaf)(20)(Leaf)))(0)`: the closure CAPTURES
//     the heap tree and embeds it, so the seed reaches {cap}; `cap` is bound to a FRESH construction (a
//     move), so the seed is a move and the copy stays elided. root = 0 + 2 × (+3) = 6.
//   * `keep`    — `bump` is seeded by `(makeId(shared))(0)`, a closure that returns the RETAINED capture
//     `shared` directly (reach {cap}, and the seed IS `shared`). `shared` is retained (`keep = shared`,
//     read afterwards), so the captured argument is NOT a move: the analysis DECLINES to elide, `bump`
//     overwrites its own deep copy, and `keep` still reads 7 (not the corrupted 207).
type Tree =
    | Leaf
    | Node(Tree, Int, Tree)

let rootVal t =
    match t with
        | Leaf -> -1
        | Node(l, v, r) -> v

let makeFresh flag =
    if flag
    then
        given (n) -> Node(Leaf)(n)(Leaf)
    else
        given (n) -> Leaf

let makeCap cap =
    if true
    then
        given (n) -> Node(cap)(n)(Leaf)
    else
        given (n) -> Leaf

let makeId cap =
    if true
    then
        given (n) -> cap
    else
        given (n) -> Leaf

let recursive grow n t =
    if n <= 0
    then t
    else
        match t with
            | Leaf -> grow(n - 1)(Node(Leaf)(1)(Leaf))
            | Node(l, v, r) -> grow(n - 1)(Node(l)(v + 1)(r))

let recursive bump n t =
    if n <= 0
    then t
    else
        match t with
            | Leaf -> bump(n - 1)(Node(Leaf)(100)(Leaf))
            | Node(l, v, r) -> bump(n - 1)(Node(l)(v + 100)(r))

let recursive outer b nb t =
    if b >= nb
    then t
    else outer(b + 1)(nb)(grow(3)(t))

let s1 = makeFresh(true)(5)

let nested = outer(0)(4)(s1)

let s2 = makeCap(Node(Leaf)(20)(Leaf))(0)

let capMove = outer(0)(2)(s2)

let shared = Node(Leaf)(7)(Leaf)

let keep = shared

let s3 = makeId(shared)(0)

let bumped = bump(2)(s3)
in Ashes.IO.print(Ashes.Text.fromInt(rootVal(nested)) + " " + Ashes.Text.fromInt(rootVal(capMove)) + " " + Ashes.Text.fromInt(rootVal(keep)) + " " + Ashes.Text.fromInt(rootVal(bumped)))
