// expect: 5 11 5050
// In-place reuse through a non-recursive helper: the loop matches a recursive-ADT accumulator and
// rebuilds it by calling `mk` (a top-level helper that constructs the node). Inside the reuse arm
// the saturated `mk` call is inlined, so its constructor reuses the dead node in place instead of
// allocating. As with direct reuse, the accumulator is deep-copied once at loop entry, so reusing
// it can't corrupt a value the caller still holds: rootVal(initial) stays 5 after loop(3) builds 11.
type Tree =
    | Leaf
    | Node(Tree, Int, Tree)

let mk l v r = Node(l)(v)(r)

let recursive loop n t = 
    if n <= 0
    then t
    else 
        match t with
            | Leaf -> loop(n - 1)(mk(Leaf)(n)(Leaf))
            | Node(l, v, r) -> loop(n - 1)(mk(l)(v + n)(r))

let rootVal t = 
    match t with
        | Leaf -> -1
        | Node(l, v, r) -> v

let initial = mk(Leaf)(5)(Leaf)

let shared = loop(3)(initial)

let big = loop(100)(Leaf)
in Ashes.IO.print(Ashes.Text.fromInt(rootVal(initial)) + " " + Ashes.Text.fromInt(rootVal(shared)) + " " + Ashes.Text.fromInt(rootVal(big)))
