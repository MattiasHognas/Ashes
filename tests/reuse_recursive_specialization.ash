// expect: 5 8 1000002
// Recursive-function in-place reuse specialization: a loop applies a recursive tree-rebuilding
// function (incAll) to its accumulator. The accumulator is deep-copied once at loop entry (so it's
// uniquely owned) and the call is routed to a generated incAll$reuse whose parameter is linear, so
// its match-then-rebuild overwrites each node in place and its self-calls recurse into incAll$reuse.
// Verifies correctness + that a caller-shared initial accumulator is not corrupted (initial stays
// 5 after loop(3) rewrites its own copy to 8). Because incAll$reuse fully reuses (every node + Leaf
// is rewritten in place), the loop also resets the arena each iteration: 50M iterations run in ~7 MB
// constant memory (a separate memory probe), versus an unbounded leak without reuse.
type Tree(A) =
    | Leaf
    | Node(Tree, A, Tree)

let rec incAll t = 
    match t with
        | Leaf -> Leaf
        | Node(l, v, r) -> Node(incAll(l))(v + 1)(incAll(r))

let rootVal t = 
    match t with
        | Leaf -> -1
        | Node(l, v, r) -> v

let rec loop n t = 
    if n <= 0
    then t
    else loop(n - 1)(incAll(t))

let initial = Node(Leaf)(5)(Leaf)

let shared = loop(3)(initial)

let big = loop(1000000)(Node(Node(Leaf)(1)(Leaf))(2)(Node(Leaf)(3)(Leaf)))
in Ashes.IO.print(Ashes.Text.fromInt(rootVal(initial)) + " " + Ashes.Text.fromInt(rootVal(shared)) + " " + Ashes.Text.fromInt(rootVal(big)))
