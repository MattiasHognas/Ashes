// A consumed list of strings searched for a head that the loop returns: the matched head is
// retained for the branch result, the literal arm is copied to the reference-counted heap beside
// it so the loop's result is uniformly runtime-managed, and the caller releases the result and
// the list it passed. Three hundred thousand searches stay at a flat plateau.
// expect: 2
let recursive build (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else build(n - 1)(Ashes.Text.fromInt(n) :: acc)

let recursive findLong (items: List(Str)) (limit: Int) =
    match items with
        | [] -> "none"
        | head :: rest ->
            if Ashes.Text.byteLength(head) > limit
            then head
            else findLong(rest)(limit)

let recursive run (round: Int) (best: Int) =
    if round == 0
    then best
    else
        match findLong(build(12)([]))(1) with
            | found ->
                run(round - 1)(if Ashes.Text.byteLength(found) > best
                then Ashes.Text.byteLength(found)
                else best)

Ashes.IO.print(Ashes.Text.fromInt(run(300000)(0)))
