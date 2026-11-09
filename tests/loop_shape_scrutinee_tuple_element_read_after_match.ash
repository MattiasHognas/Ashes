// expect: 12
type Kind =
    | CallKnown(Int, Str, Int)
    | Other(Int)
    | Switch(Int, List(Int))

type IrInstruction =
    | instruction: Kind

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

let retarget (stackEnv: Int) (stackAllocated: Bool) irInst =
    match irInst with
        | IrInstruction { instruction = CallKnown(dest, label, _) } -> IrInstruction(instruction = CallKnown(dest)(label)(stackEnv))
        | _ -> IrInstruction(instruction = Other(0))

let recursive collect indexed remaining (nextTemp: Int) rewrites =
    match remaining with
        | [] -> (rewrites, nextTemp)
        | (position, irInst) :: tail ->
            match (irInst, 0) with
                | (IrInstruction { instruction = _ }, resultTemp) as pair ->
                    let callPosition = position + 1
                    in
                        let retargeted =
                            match lookupAssociation(callPosition)(indexed) with
                                | Some(nextCall) -> retarget(nextTemp)(true)(nextCall)
                                | None ->
                                    match pair with
                                        | (kept, _) -> kept
                        in
                            rewrites
                            |> setAssociation(callPosition)(retargeted)
                            |> collect(indexed)(tail)(nextTemp + 1)
                | _ -> collect(indexed)(tail)(nextTemp)(rewrites)

let recursive indexInstructions (insts: List(IrInstruction)) (n: Int) acc =
    match insts with
        | [] -> Ashes.Collection.List.reverse(acc)
        | i :: rest -> indexInstructions(rest)(n + 1)((n, i) :: acc)

let once (insts: List(IrInstruction)) (tempCount: Int) =
    (let indexed = indexInstructions(insts)(0)([])
    in
        match collect(indexed)(indexed)(tempCount)([]) with
            | (rewrites, nextTemp) -> (insts, nextTemp > 0 && Ashes.Collection.List.length(rewrites) > 0))

let recursive weigh (insts: List(IrInstruction)) =
    match insts with
        | [] -> 0
        | IrInstruction { instruction = CallKnown(_, label, _) } :: rest -> Ashes.Text.byteLength(label) + weigh(rest)
        | _ :: rest -> 1 + weigh(rest)

let probe (n: Int) =
    (let insts = [IrInstruction(instruction = CallKnown(1)("s" + Ashes.Text.fromInt(n))(2)), IrInstruction(instruction = CallKnown(5)("t" + Ashes.Text.fromInt(n))(6)), IrInstruction(instruction = CallKnown(9)("u")(1)), IrInstruction(instruction = Other(n))]
    in
        match once(insts)(10) with
            | (rewritten, _) -> weigh(rewritten))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(2)
|> Ashes.IO.print
