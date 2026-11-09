// expect: 1
let recursive containsLabel (label: Str) (labels: List(Str)) =
    match labels with
        | [] -> false
        | l :: rest ->
            if l == label
            then true
            else containsLabel(label)(rest)

let recursive resolveGroups groups known conflicting changed =
    match groups with
        | [] -> (known, conflicting, changed)
        | (label, index) :: tail ->
            if containsLabel(label)(conflicting)
            then resolveGroups(tail)(known)(conflicting)(changed)
            else resolveGroups(tail)(known)(label :: conflicting)(index > 0)

let recursive computeCaptured groups known conflicting rounds =
    match resolveGroups(groups)(known)(conflicting)(false) with
        | (nextKnown, nextConflicting, true) ->
            if rounds > 50
            then 1000
            else computeCaptured(groups)(nextKnown)(nextConflicting)(rounds + 1)
        | (_, _, false) -> rounds

let probe (n: Int) = computeCaptured([("a", n), ("b", 2), ("c", 3)])([])([])(0)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(1)
|> Ashes.IO.print
