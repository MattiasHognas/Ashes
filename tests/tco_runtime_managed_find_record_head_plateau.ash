// A consumed list of records searched for a head that the loop returns beside a static record
// arm: the matched head is retained for the branch result, the static record arm is built in
// the arena and deep-copied to the reference-counted heap beside it so the loop's result is
// uniformly runtime-managed, and the caller releases the result and the list it passed. Three
// hundred thousand searches stay at a flat plateau.
// expect: 8
type Item =
    | name: Str
    | weight: Int

let recursive build (n: Int) (acc: List(Item)) =
    if n == 0
    then acc
    else build(n - 1)(Item(name = Ashes.Text.fromInt(n), weight = n) :: acc)

let recursive findHeavy (items: List(Item)) (limit: Int) =
    match items with
        | [] -> Item(name = "none", weight = 0)
        | head :: rest ->
            if head.weight > limit
            then head
            else findHeavy(rest)(limit)

let recursive run (round: Int) (best: Int) =
    if round == 0
    then best
    else
        match findHeavy(build(12)([]))(6) with
            | found ->
                run(round - 1)(if found.weight + Ashes.Text.byteLength(found.name) > best
                then found.weight + Ashes.Text.byteLength(found.name)
                else best)

Ashes.IO.print(Ashes.Text.fromInt(run(300000)(0)))
