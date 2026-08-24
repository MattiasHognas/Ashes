// expect: 2
// Dead-arm elimination regression: a
// match on a recursive function's own parameter has an unresolved scrutinee type at the point
// dead-arm trimming runs (its type is only pinned down by this same match's own arm-by-arm
// unification, still ahead at that point) — trimming must decline rather than guess, exactly as
// EmitMatchExhaustivenessDiagnostics already safely does for the "Missing case" diagnostic. A
// naive trim gated the wrong way here previously dropped the live Node arm entirely and produced
// a wrong answer instead of merely missing an optimization.
type Tree =
    | Leaf
    | Node(Tree, Int, Tree)

let recursive depth tree =
    match tree with
        | Leaf -> 0
        | Node(left, _, right) ->
            let leftDepth = depth(left)
            in
                let rightDepth = depth(right)
                in
                    1 + (if leftDepth > rightDepth
                    then leftDepth
                    else rightDepth)
        | _ -> 999

let sample = Node(Node(Leaf)(1)(Leaf))(2)(Leaf)
in Ashes.IO.print(depth(sample))
