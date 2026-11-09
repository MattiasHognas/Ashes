// expect: 27
// A walk with string labels records snapshots through an Eq-constrained association list, by way of
// a constrained wrapper and a generic loop over switch cases. Each is lowered again at the walk's
// concrete types, where the lookup's Some and the replaced entries are released.
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

type Inst =
    | Switch(Int, List(Case), Str)
    | Goto(Str)
    | Nop

let recursive walk (insts: List(Inst)) (n: Int) saved =
    match insts with
        | [] -> saved
        | inst :: rest ->
            let facts = Facts(ints = [(n, n)], bools = [(n, true)])
            in
                match inst with
                    | Switch(_tagTemp, cases, defaultLabel) ->
                        saved
                        |> recordSwitchCaseSnapshots(cases)(facts)
                        |> recordEdgeSnapshot(defaultLabel)(facts)
                        |> walk(rest)(n + 1)
                    | Goto(target) ->
                        saved
                        |> recordEdgeSnapshot(target)(facts)
                        |> walk(rest)(n + 1)
                    | Nop -> walk(rest)(n + 1)(saved)

let recursive count (saved: List((Str, List(Facts)))) (acc: Int) =
    match saved with
        | [] -> acc
        | (_, snapshots) :: rest -> count(rest)(acc + Ashes.Collection.List.length(snapshots))

let program = [Goto("a"), Switch(1)([Case(tag = 0, label = "a"), Case(tag = 1, label = "b"), Case(tag = 2, label = "b")])("c"), Nop, Switch(2)([Case(tag = 0, label = "c"), Case(tag = 1, label = "a")])("a"), Goto("b")]

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        loop(n - 1)(acc + count(walk(program)(0)([]))(0))
in
    0
    |> loop(3)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
