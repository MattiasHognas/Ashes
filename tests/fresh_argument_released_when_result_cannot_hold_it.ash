// A fresh list of records is handed to a callee whose result, a table of names, has no place for a
// value of the argument's type: a callee that did not adopt the argument left it with the caller,
// which releases it after the call.
// expect: 27
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
                | [] -> addKnown(rest)(known)

let recursive runKnown facts known passes =
    if passes == 0
    then known
    else
        runKnown(facts)(addKnown(facts)(known))(passes - 1)

let knownLabels (items: List(Int)) =
    runKnown(withFacts(items)([]))([])(3)

let recursive total (names: List((Str, Int))) (sum: Int) =
    match names with
        | [] -> sum
        | (name, first) :: rest -> total(rest)(sum + Ashes.Text.byteLength(name) + first)

0
|> total(knownLabels([1, 2, 3]))
|> Ashes.IO.print
