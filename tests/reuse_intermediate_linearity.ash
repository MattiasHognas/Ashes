// expect: 5 8 1000002
// Intermediate-value linearity: each rebuild level passes a freshly in-place-reused node through a
// second helper (bal) that matches and rebuilds it again — like an AVL balance's normalized =
// makeNode(...). Because the value handed to bal was built by reuse (and used once), bal's parameter
// is treated as linear too, so its rebuild reuses the same cell instead of allocating. The whole
// rewrite stays below the watermark, so a pure-rewrite fold runs in constant memory (50M iters ~7 MB,
// separate probe). Verifies correctness + caller-shared accumulator uncorrupted (5 -> stays 5).
type Tree(A) =
    | Leaf
    | Node(Tree, A, Tree)

let mk l v r = Node(l)(v)(r)

let bal t = 
    match t with
        | Leaf -> Leaf
        | Node(l, v, r) -> mk(l)(v)(r)

let upd by = 
    (let recursive go t = 
        match t with
            | Leaf -> Leaf
            | Node(l, v, r) -> bal(mk(go(l))(v + by)(go(r)))
    in go)

let rootVal t = 
    match t with
        | Leaf -> -1
        | Node(l, v, r) -> v

let recursive loop n t = 
    if n <= 0
    then t
    else loop(n - 1)(upd(1)(t))

let initial = Node(Leaf)(5)(Leaf)

let shared = loop(3)(initial)

let big = loop(1000000)(Node(Node(Leaf)(1)(Leaf))(2)(Node(Leaf)(3)(Leaf)))
in Ashes.IO.print(Ashes.Text.fromInt(rootVal(initial)) + " " + Ashes.Text.fromInt(rootVal(shared)) + " " + Ashes.Text.fromInt(rootVal(big)))
