// The program entry binds a list of switch cases and a facts record with `let`, and passes both to a
// generic Eq-constrained loop that records snapshots in an association list. A let-bound value of a
// closed type settles the loop's parameter types like a function's parameter does, so the loop is
// lowered again at those types, where the lookup's Some and the replaced entries are released.
// expect: 900
type Facts =
    | ints: List((Int, Int))
    | bools: List((Int, Bool))

let recursive lookupAssociation key entries =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupAssociation(key)(tail)

let recursive setAssociation key value entries =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setAssociation(key)(value)(tail)

let recordEdgeSnapshot target facts saved =
    match lookupAssociation(target)(saved) with
        | Some(snapshots) ->
            setAssociation(target)(Ashes.Collection.List.append(snapshots)([facts]))(saved)
        | None -> setAssociation(target)([facts])(saved)

type Case =
    | tag: Int
    | label: Str
    deriving {Eq, Show}

let switchCaseLabel (switchCase: Case) = switchCase.label

let recursive recordSwitchCaseSnapshots cases facts saved =
    match cases with
        | [] -> saved
        | switchCase :: tail ->
            saved
            |> recordEdgeSnapshot(switchCaseLabel(switchCase))(facts)
            |> recordSwitchCaseSnapshots(tail)(facts)

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
    else makeCases(n - 1)(Case(tag = n, label = labelOf(n)) :: acc)

let recursive count (saved: List((Str, List(Facts)))) (acc: Int) =
    match saved with
        | [] -> acc
        | (_, snapshots) :: rest -> count(rest)(acc + Ashes.Collection.List.length(snapshots))

let cases = makeCases(900)([])

let facts = Facts(ints = [(1, 1)], bools = [(1, true)])

let saved = recordSwitchCaseSnapshots(cases)(facts)([])

0
|> count(saved)
|> Ashes.Text.fromInt
|> Ashes.IO.print
