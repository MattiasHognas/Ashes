// expect: 111
type LineTree =
    | LineLeaf
    | LineNode(LineTree, Int, Int, LineTree)

let recursive buildLineTree (starts: List(Int)) (firstLine: Int) (count: Int) =
    if count <= 0
    then (LineLeaf, starts)
    else
        let leftCount = count / 2
        in
            match buildLineTree(starts)(firstLine)(leftCount) with
                | (left, start :: afterLeft) ->
                    match buildLineTree(afterLeft)(firstLine + leftCount + 1)(count - leftCount - 1) with
                        | (right, afterRight) -> (LineNode(left)(firstLine + leftCount)(start)(right), afterRight)
                | (left, []) -> (left, [])

let recursive size (tree: LineTree) =
    match tree with
        | LineLeaf -> 0
        | LineNode(left, _line, start, right) -> 1 + start + size(left) + size(right)

let probe (n: Int) =
    match buildLineTree([1, 5, 9, n, n + 3, n + 7])(2)(6) with
        | (tree, _remaining) -> size(tree)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(3)
|> Ashes.IO.print
