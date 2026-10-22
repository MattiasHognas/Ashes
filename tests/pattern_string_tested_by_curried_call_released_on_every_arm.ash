// A loop over boxes tests each box's label, bound under a cons pattern, with a curried library call
// and branches on the result. The call's result counts as a use of the label, and it is last read by
// the branch itself: the label's release goes before the branch, so both arms take it.
// expect: 3
type Box =
    | label: Str
    | tag: Int

let recursive countStarting (boxes: List(Box)) (count: Int) =
    match boxes with
        | [] -> count
        | Box { label = label } :: rest ->
            if Ashes.Text.startsWith(label)("A")
            then countStarting(rest)(count + 1)
            else countStarting(rest)(count)

let recursive countPrefixed (boxes: List(Box)) (count: Int) =
    match boxes with
        | [] -> count
        | Box { label = label } :: rest ->
            if Ashes.Text.startsWith("A")(label)
            then countPrefixed(rest)(count + 1)
            else countPrefixed(rest)(count)

let recursive boxesFrom (labels: List(Str)) (built: List(Box)) =
    match labels with
        | [] -> built
        | label :: rest -> boxesFrom(rest)(Box(label = label + "", tag = 1) :: built)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let boxes = boxesFrom(["A-full", "Bee", "A-empty", "Main"])([])
        in rounds(n - 1)(total + countStarting(boxes)(0) + countPrefixed(boxes)(0) - 1)

0
|> rounds(3)
|> Ashes.IO.print
