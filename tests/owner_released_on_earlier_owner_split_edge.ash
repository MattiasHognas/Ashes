// expect: 33
// A loop binds a record's name and its list of uses, matches the list, and under a condition skips
// the name. The release of the matched list's head binding splits the edges out of the
// accumulator's release loop first; the name's release reaches those split edges too, so every
// round's skipped name is freed and the kept names still read back.
type Fact =
    | name: Str
    | uses: List(Int)

let recursive withFacts items acc =
    match items with
        | [] -> acc
        | item :: rest -> withFacts(rest)(Fact(name = "f" + Ashes.Text.fromInt(item), uses = [item, item + 1]) :: acc)

let recursive addKnown facts known =
    match facts with
        | [] -> known
        | Fact { name = name, uses = uses } :: rest ->
            match uses with
                | first :: _ ->
                    if first > 1
                    then addKnown(rest)((name, first) :: known)
                    else addKnown(rest)(known)
                | [] -> addKnown(rest)((name, 0) :: known)

let knownLabels (items: List(Int)) =
    addKnown(withFacts(items)([]))([])

let recursive total (names: List((Str, Int))) (sum: Int) =
    match names with
        | [] -> sum
        | (name, first) :: rest -> total(rest)(sum + Ashes.Text.byteLength(name) + first)

let recursive rounds (n: Int) (sum: Int) =
    if n == 0
    then sum
    else
        rounds(n - 1)(sum + total(knownLabels([1, 2, n + 3]))(0))

0
|> rounds(3)
|> Ashes.IO.print
