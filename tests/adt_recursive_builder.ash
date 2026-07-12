// expect: 15
// A recursive function that builds a self-recursive ADT by embedding its own recursive result in a
// constructor field must type-check. The self-referential payload `Tree` (and the primitive payload
// `Int`) are concrete field types, not inferred type parameters. Before the fix, migration-compat
// implicit type-parameter inference treated the declaring type's own name and primitive payload names
// as fresh type parameters, which made the self-recursive field polymorphic and failed HM inference
// with an occurs-check error the moment the type was actually built recursively.
type Tree =
    | Leaf
    | Node(Tree, Int, Tree)

let recursive sumTree t =
    match t with
        | Leaf -> 0
        | Node(l, v, r) -> sumTree(l) + v + sumTree(r)

let recursive build n =
    if n <= 0
    then Leaf
    else Node(build(n - 1))(n)(Leaf)

let t = build(5)
in Ashes.IO.print(Ashes.Text.fromInt(sumTree(t)))
