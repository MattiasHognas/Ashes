// binary-trees -- Benchmarks Game challenge.
//
// Allocate and immediately discard an enormous number of short-lived binary trees. For each depth
// from minDepth (4) to maxDepth in steps of 2, build many trees, walk each for a node-count
// checksum, and let it go; a single long-lived tree stays resident across the whole run. The work
// is pure allocation churn of a recursive ADT -- the small, isolated reproducer for the non-GC
// bump-arena reclamation path (challenges/1brc FLAWS #2).
//
// Usage: ./binary-trees 21   (defaults to 10)
import Ashes.IO as io
import Ashes.Text as text
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

let recursive pow2 k = 
    if k == 0
    then 1
    else 2 * pow2(k - 1)

let recursive sumChecks depth n acc = 
    if n == 0
    then acc
    else sumChecks depth(n - 1)(check(make(depth)) + acc)

let recursive depthLoop depth maxDepth minDepth out = 
    if depth > maxDepth
    then out
    else 
        let iterations = pow2(maxDepth - depth + minDepth)
        in 
            let sum = sumChecks depth iterations 0
            in 
                let line = text.fromInt(iterations) + "\t trees of depth " + text.fromInt(depth) + "\t check: " + text.fromInt(sum) + "\n"
                in depthLoop(depth + 2)(maxDepth)(minDepth)(out + line)

let run n = 
    (let minDepth = 4
    in 
        let maxDepth = 
            if minDepth + 2 > n
            then minDepth + 2
            else n
        in 
            let stretchDepth = maxDepth + 1
            in 
                let longLived = make(maxDepth)
                in 
                    let stretchLine = "stretch tree of depth " + text.fromInt(stretchDepth) + "\t check: " + text.fromInt(check(make(stretchDepth))) + "\n"
                    in 
                        let body = depthLoop(minDepth)(maxDepth)(minDepth)("")
                        in 
                            let longLine = "long lived tree of depth " + text.fromInt(maxDepth) + "\t check: " + text.fromInt(check(longLived)) + "\n"
                            in stretchLine + body + longLine)

match io.args with
    | arg :: _ -> 
        match text.parseInt(arg) with
            | Ok(n) -> io.print(run(n))
            | Error(_) -> io.print(run(10))
    | [] -> io.print(run(10))
