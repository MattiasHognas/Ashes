// expect: 262112
// Regression for the RC Perceus migration's binary-trees regression (documented CO-38 in
// docs/md/internals/changelog.md): a self-recursive ADT whose base-case arm is a bare nullary
// constructor (Leaf) and whose recursive arm builds a DIFFERENT constructor (Node) from ordinary
// function calls (not nested constructor literals) used to get a MIXED representation. The escaping-
// result analysis flagged the whole function "fresh" off the trivially-fresh Leaf arm alone, so Leaf
// was promoted to an RC cell while Node (whose children come from recursive calls, not literal
// constructor trees) stayed arena. Every Node built by `make` is arena-managed and its drop is a
// no-op, so it never walks into its Leaf children -- every Leaf reachable through a discarded tree
// leaked its RC cell forever. `check(make(depth))` here builds and immediately discards a tree every
// iteration inside a TCO loop; before the fix this leaked ~2^depth RC cells per iteration (measured:
// depth 14, 200,000 iterations -> 57 GB peak RSS and no completion in reasonable time). After the fix
// the whole tree (Leaf and Node alike) is arena-managed and reclaimed by the loop's per-iteration
// arena reset, so this now completes in well under a second at a few MB.
type Tree =
    | Leaf
    | Node(Tree, Tree)

let recursive make depth =
    if depth == 0
    then Leaf
    else Node(make(depth - 1))(make(depth - 1))

let recursive check tree =
    match tree with
        | Leaf -> 1
        | Node(l, r) -> 1 + check(l) + check(r)

let recursive loop n acc =
    if n == 0
    then acc
    else loop(n - 1)(check(make(12)) + acc)

Ashes.IO.print(Ashes.Text.fromInt(loop(32)(0)))
