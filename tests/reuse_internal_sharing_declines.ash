// expect: 12 9
// CO-2 result-alias elision — internal-sharing soundness guard (fresh cell embedded twice).
//
// `share` builds a FRESH cell `x` and embeds it into TWO heap fields of the returned node:
// `let x = Node(Leaf)(k)(Leaf) in Node(x)(0)(x)`. The result has two heap paths to the same fresh
// cell — it is INTERNALLY SHARED, hence NOT uniquely owned, even though `x` is not a parameter. The
// entry deep-copy of a reuse fold exists precisely to unshare such a structure, so eliding it would
// leave the sharing intact and a fold rebuilding both paths in place could corrupt.
//
// The may-alias reach summary gives every locally-introduced binding (`let x`) a per-binding identity
// token summed into its reach, so `Node(x)(0)(x)` sums that token to the cap and POISONS `share` —
// exactly as a doubled *parameter* (`Node(p)(0)(p)`) would. `share(k)` is therefore never a move, so
// the direct-reuse fold `sgrow` (seeded via `souter` by `share(9)`) keeps its entry copy (DECLINED).
// The kept copy unshares the structure; the program runs correctly. root = 0 + 4 batches × (+3) = 12,
// and the (intact) left child still reads 9.
type Tree =
    | Leaf
    | Node(Tree, Int, Tree)

let rootVal t = 
    match t with
        | Leaf -> -1
        | Node(l, v, r) -> v

let leftVal t = 
    match t with
        | Leaf -> -1
        | Node(l, v, r) -> rootVal(l)

let share k = 
    (let x = Node(Leaf)(k)(Leaf)
    in Node(x)(0)(x))

let recursive sgrow n t = 
    if n <= 0
    then t
    else 
        match t with
            | Leaf -> sgrow(n - 1)(Node(Leaf)(1)(Leaf))
            | Node(l, v, r) -> sgrow(n - 1)(Node(l)(v + 1)(r))

let recursive souter b nb t = 
    if b >= nb
    then t
    else souter(b + 1)(nb)(sgrow(3)(t))

let shared = souter(0)(4)(share(9))
in Ashes.IO.print(Ashes.Text.fromInt(rootVal(shared)) + " " + Ashes.Text.fromInt(leftVal(shared)))
