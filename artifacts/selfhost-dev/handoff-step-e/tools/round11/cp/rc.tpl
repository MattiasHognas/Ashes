type Kind =
    | MakeClosure(Int, Str)
    | CallKnown(Int, Str)
    | Return(Int)
    | Other(Int)

type Fn =
    | label: Str
    | instructions: List(Kind)

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

let knownClosureLabelOf sourceTemp singleDefs knownLabels =
    match lookupAssociation(sourceTemp)(singleDefs) with
        | Some(MakeClosure(_, fnLabel)) -> Some(fnLabel)
        | Some(CallKnown(_, fnLabel)) -> lookupAssociation(fnLabel)(knownLabels)
        | _ -> None

let recursive returnedClosureLabel instructions singleDefs knownLabels current sawReturn =
    match instructions with
        | [] ->
            if sawReturn
            then current
            else None
        | Return(source) :: tail ->
            match knownClosureLabelOf(source)(singleDefs)(knownLabels) with
                | None -> None
                | Some(label) ->
                    match current with
                        | None -> returnedClosureLabel(tail)(singleDefs)(knownLabels)(Some(label))(true)
                        | Some(previous) ->
                            if previous == label
                            then returnedClosureLabel(tail)(singleDefs)(knownLabels)(current)(true)
                            else None
        | _ :: tail -> returnedClosureLabel(tail)(singleDefs)(knownLabels)(current)(sawReturn)

let recursive singleDefsOf instructions acc =
    match instructions with
        | [] -> acc
        | MakeClosure(t, l) :: tail -> singleDefsOf(tail)((t, MakeClosure(t, l)) :: acc)
        | CallKnown(t, l) :: tail -> singleDefsOf(tail)((t, CallKnown(t, l)) :: acc)
        | _ :: tail -> singleDefsOf(tail)(acc)

let recursive addKnown (fns: List(Fn)) knownLabels changed =
    match fns with
        | [] -> (knownLabels, changed)
        | fn :: rest ->
            match lookupAssociation(fn.label)(knownLabels) with
                | Some(_) -> addKnown(rest)(knownLabels)(changed)
                | None ->
                    match returnedClosureLabel(fn.instructions)(singleDefsOf(fn.instructions)([]))(knownLabels)(None)(false) with
                        | Some(label) -> addKnown(rest)(setAssociation(fn.label)(label)(knownLabels))(true)
                        | None -> addKnown(rest)(knownLabels)(changed)

let recursive runKnown fns knownLabels =
    match addKnown(fns)(knownLabels)(false) with
        | (next, true) -> runKnown(fns)(next)
        | (next, false) -> next

let name (prefix: Str) (n: Int) = prefix + Ashes.Text.fromInt(n)

let recursive buildFns (n: Int) (acc: List(Fn)) =
    if n == 0
    then acc
    else
        buildFns(n - 1)(Fn(label = name("f")(n), instructions = [MakeClosure(1, name("lambda_")(n)), CallKnown(2, name("f")(n + 1)), Return(if n % 2 == 0 then 1 else 2), Other(3)]) :: acc)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + Ashes.Collection.List.length(runKnown(buildFns(12)([]))([])))

Ashes.IO.print(Ashes.Text.fromInt(loop(ROUNDS * 20)(0)))
