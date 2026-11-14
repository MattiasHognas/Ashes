// A generic loop records one association per switch case through an Eq-constrained helper, and is
// called with a literal and a call result as arguments. No variable carries the argument types, so
// the call site itself fixes the loop's parameter types and the loop is lowered again at them, where
// each case's label is released once its entry has been replaced.
// expect: 3 21
let recursive setAssociation key value entries =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setAssociation(key)(value)(tail)

type Case =
    | tag: Int
    | label: Str

let switchCaseLabel (switchCase: Case) = switchCase.label

let recursive recordSwitchCases cases facts saved =
    match cases with
        | [] -> saved
        | switchCase :: tail ->
            saved
            |> setAssociation(switchCaseLabel(switchCase))(facts)
            |> recordSwitchCases(tail)(facts)

let labelOf (n: Int) =
    if n % 3 == 0
    then "a"
    else
        if n % 3 == 1
        then "b"
        else "c"

let recursive makeCases (n: Int) (acc: List(Case)) =
    if n == 0
    then acc
    else makeCases(n - 1)(Case(tag = n, label = labelOf(n) + "") :: acc)

let recursive total (saved: List((Str, Int))) (acc: Int) =
    match saved with
        | [] -> acc
        | (_, value) :: rest -> total(rest)(acc + value)

let saved =
    recordSwitchCases(makeCases(900)([]))(7)([])

Ashes.IO.print(Ashes.Text.fromInt(Ashes.Collection.List.length(saved)) + " " + Ashes.Text.fromInt(total(saved)(0)))
