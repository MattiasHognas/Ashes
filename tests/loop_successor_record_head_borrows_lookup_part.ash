// expect: 180
// The optimizer's devirtualizing rewrite: a call through a closure whose creator's returned label
// is known becomes a direct call. The label matched out of the lookup's owned result goes into an
// instruction record written as the head of the cell consed onto the accumulator, nested in a
// constructor of the record's field. The back edge copies the cell with references of its own, so
// neither the record nor its field keeps the retain it took. The labels are read after the loop.
type Kind =
    | CallClosure(Int, Int)
    | CallKnown(Int, Str)
    | LoadMem(Int, Int)
    | Other(Int)

type Inst =
    | instruction: Kind
    | location: Int

let recursive lookupKind (key: Int) (entries: List((Int, Kind))) =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupKind(key)(tail)

let recursive lookupLabel (key: Str) (entries: List((Str, Str))) =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupLabel(key)(tail)

let recursive devirtualize insts singleDefs knownLabels nextTemp acc changed =
    match insts with
        | [] -> (Ashes.Collection.List.reverse(acc), nextTemp, changed)
        | (Inst { instruction = CallClosure(dest, closureTemp), location = loc } as inst) :: tail ->
            match lookupKind(closureTemp)(singleDefs) with
                | Some(CallKnown(_, calleeLabel)) ->
                    match lookupLabel(calleeLabel)(knownLabels) with
                        | Some(label) -> devirtualize(tail)(singleDefs)(knownLabels)(nextTemp + 1)(Inst(instruction = CallKnown(dest)(label), location = loc) :: Inst(instruction = LoadMem(nextTemp)(closureTemp), location = loc) :: acc)(true)
                        | None -> devirtualize(tail)(singleDefs)(knownLabels)(nextTemp)(inst :: acc)(changed)
                | _ -> devirtualize(tail)(singleDefs)(knownLabels)(nextTemp)(inst :: acc)(changed)
        | head :: tail -> devirtualize(tail)(singleDefs)(knownLabels)(nextTemp)(head :: acc)(changed)

let name (prefix: Str) (n: Int) = prefix + Ashes.Text.fromInt(n)

let recursive program (n: Int) acc =
    if n == 0
    then acc
    else
        program(n - 1)(Inst(instruction = n
        |> name("f")
        |> CallKnown(n * 2), location = n) :: Inst(instruction = CallClosure(n * 2 + 1)(n * 2), location = n) :: acc)

let recursive defs (n: Int) acc =
    if n == 0
    then acc
    else
        defs(n - 1)((n * 2, n
        |> name("f")
        |> CallKnown(n * 2)) :: acc)

let recursive labels (n: Int) acc =
    if n == 0
    then acc
    else labels(n - 1)((name("f")(n), name("lambda_")(n)) :: acc)

let recursive labelBytes (insts: List(Inst)) (total: Int) =
    match insts with
        | [] -> total
        | Inst { instruction = CallKnown(_, label) } :: rest -> labelBytes(rest)(total + Ashes.Text.byteLength(label))
        | _ :: rest -> labelBytes(rest)(total)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        match devirtualize(program(6)([]))(defs(6)([]))(labels(6)([]))(100)([])(false) with
            | (rewritten, _, _) -> loop(n - 1)(acc + labelBytes(rewritten)(0))

0
|> loop(3)
|> Ashes.Text.fromInt
|> Ashes.IO.print
