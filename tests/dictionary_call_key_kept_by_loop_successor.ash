// expect: 128
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

type Kind =
    | SwitchTag(Int, List(Case), Str)
    | Jump(Str)
    | Other(Int)

type Instruction =
    | instruction: Kind
    | location: Maybe(Int)

let foldConstants (instructions: List(Instruction)) =
    (let marker = Ashes.Collection.List.length(instructions)
    in
        let recursive foldLoop insts facts saved prevTerm acc =
            match insts with
                | [] -> (Ashes.Collection.List.reverse(acc), saved)
                | (Instruction { instruction = inst, location = loc } as irInst) :: tail ->
                    match inst with
                        | SwitchTag(tagTemp, cases, defaultLabel) ->
                            match lookupAssociation(tagTemp)(facts.ints) with
                                | Some(_tag) ->
                                    foldLoop(tail)(facts)(recordEdgeSnapshot(defaultLabel)(facts)(saved))(true)(Instruction(instruction = Jump(defaultLabel), location = loc) :: acc)
                                | None ->
                                    foldLoop(tail)(facts)(saved
                                    |> recordSwitchCaseSnapshots(cases)(facts)
                                    |> recordEdgeSnapshot(defaultLabel)(facts))(true)(irInst :: acc)
                        | Jump(target) ->
                            foldLoop(tail)(facts)(recordEdgeSnapshot(target)(facts)(saved))(true)(irInst :: acc)
                        | Other(value) -> foldLoop(tail)(Facts(ints = [(value, marker)], bools = facts.bools))(saved)(false)(irInst :: acc)
        in foldLoop(instructions)(Facts(ints = [], bools = []))([])(false)([]))

let programOf (n: Int) =
    [
        Instruction(instruction = Other(n), location = None),
        Instruction(instruction = SwitchTag(7)([Case(tag = 0, label = "rc_normalize_adt_" + Ashes.Text.fromInt(n)), Case(tag = 1, label = "rc_normalize_end_" + Ashes.Text.fromInt(n)), Case(tag = 2, label = "rc_normalize_end_" + Ashes.Text.fromInt(n))])("default_" + Ashes.Text.fromInt(n)), location = Some(n)),
        Instruction(instruction = Jump("default_" + Ashes.Text.fromInt(n)), location = None),
        Instruction(instruction = SwitchTag(8)([Case(tag = 0, label = "default_" + Ashes.Text.fromInt(n))])("x"), location = None)
    ]

let recursive count (saved: List((Str, List(Facts)))) (acc: Int) =
    match saved with
        | [] -> acc
        | (_, snapshots) :: rest -> count(rest)(acc + Ashes.Collection.List.length(snapshots))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        match n
        |> programOf
        |> foldConstants with
            | (folded, saved) -> loop(n - 1)(acc + Ashes.Collection.List.length(folded) + count(saved)(0))
in
    0
    |> loop(12)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
