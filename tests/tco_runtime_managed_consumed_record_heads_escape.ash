// A consumed list of records whose matched heads escape into a sibling record accumulator lives
// on the reference-counted heap: the list is normalized at entry, each back edge releases the
// consumed cell, the head stored into the successor is retained by its pattern owner and released
// through the record's structural dropper once the arm ends, and the exit hands the accumulator to
// the caller, which releases it. Three hundred thousand rounds stay at a flat plateau.
// expect: 4200000
type Item =
    | name: Str
    | weight: Int

type Best =
    | item: Item
    | score: Int

let recursive build (n: Int) (acc: List(Item)) =
    if n == 0
    then acc
    else build(n - 1)(Item(name = Ashes.Text.fromInt(n), weight = n) :: acc)

let recursive heaviest (items: List(Item)) (best: Best) =
    match items with
        | [] -> best
        | head :: rest ->
            if head.weight > best.score
            then heaviest(rest)(Best(item = head, score = head.weight))
            else heaviest(rest)(best)

let recursive run (round: Int) (total: Int) =
    if round == 0
    then total
    else
        match heaviest(build(12)([]))(Best(item = Item(name = "none", weight = 0), score = 0)) with
            | found ->
                match found.item with
                    | item -> run(round - 1)(total + found.score + Ashes.Text.byteLength(item.name))

Ashes.IO.print(Ashes.Text.fromInt(run(300000)(0)))
