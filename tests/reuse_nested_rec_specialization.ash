// expect: 5 8 1000002
// In-place reuse specialization of the Map.set shape: a multi-parameter function that returns a
// nested recursive single-parameter function (let f a = (let recursive go t = ... in go)). A loop applying
// it to its accumulator (loop(...)(upd(1)(t))) deep-copies the accumulator once at entry, specializes
// upd into upd$reuse whose nested go has a linear parameter (so its match-then-rebuild reuses nodes
// in place, helper rebuilds inline), and routes the call there. With a pure-rewrite body this runs in
// constant memory (50M iters in ~7 MB, separate probe). Verifies correctness + a caller-shared
// accumulator is not corrupted (initial stays 5 after loop(3) rewrites its own copy to 8).
type Tree(A) =
    | Leaf
    | Node(Tree, A, Tree)

let mk l v r = Node(l)(v)(r)

let upd by =
    (let recursive go t =
        match t with
            | Leaf -> Leaf
            | Node(l, v, r) -> mk(go(l))(v + by)(go(r))
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
