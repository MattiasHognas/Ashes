// expect: 5 11 5050
// In-place reuse of a TCO accumulator's nodes: the loop matches a recursive ADT accumulator and
// rebuilds it with the same constructor, so the node is overwritten in place rather than
// reallocated each iteration (constant memory; verified separately under a memory probe). The
// accumulator is deep-copied once at loop entry so reusing it can never corrupt a value the caller
// still holds — `rootVal(initial)` stays 5 after `loop(3)(initial)` mutates its own copy to 11.
type Tree =
    | Leaf
    | Node(Tree, Int, Tree)

let recursive loop n t = 
    if n <= 0
    then t
    else 
        match t with
            | Leaf -> loop(n - 1)(Node(Leaf)(n)(Leaf))
            | Node(l, v, r) -> loop(n - 1)(Node(l)(v + n)(r))

let rootVal t = 
    match t with
        | Leaf -> -1
        | Node(l, v, r) -> v

let initial = Node(Leaf)(5)(Leaf)

let shared = loop(3)(initial)

let big = loop(100)(Leaf)
in Ashes.IO.print(Ashes.Text.fromInt(rootVal(initial)) + " " + Ashes.Text.fromInt(rootVal(shared)) + " " + Ashes.Text.fromInt(rootVal(big)))
