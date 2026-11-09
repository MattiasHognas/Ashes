// expect: 12
type Loc =
    | filePath: Str
    | line: Int

type Kind =
    | CallKnown(Int, Str, Int, Int, Int, Bool)
    | LoadEnv(Int, Int)
    | Other(Int)
    | Switch(Int, List(Int))

type IrInstruction =
    | instruction: Kind
    | location: Maybe(Loc)

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
        | IrInstruction { instruction = CallKnown(dest, label, _, argTemp, flagTemp, _), location = loc } -> IrInstruction(instruction = CallKnown(dest)(label)(stackEnv)(argTemp)(flagTemp)(stackAllocated), location = loc)
        | _ -> IrInstruction(instruction = Other(0), location = None)

let recursive collect indexed remaining (nextTemp: Int) rewrites =
    match remaining with
        | [] -> (rewrites, nextTemp)
        | (position, irInst) :: tail ->
            match (irInst, 0) with
                | (IrInstruction { location = loc }, resultTemp) ->
                    let callPosition = position + 1
                    in
                        let retargeted =
                            match lookupAssociation(callPosition)(indexed) with
                                | Some(nextCall) -> retarget(nextTemp)(true)(nextCall)
                                | None -> irInst
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
        | IrInstruction { instruction = CallKnown(_, label, _, _, _, _) } :: rest -> Ashes.Text.byteLength(label) + weigh(rest)
        | _ :: rest -> 1 + weigh(rest)

let probe (n: Int) =
    (let insts = [IrInstruction(instruction = CallKnown(1)("s" + Ashes.Text.fromInt(n))(2)(3)(4)(false), location = Some(Loc(filePath = "a", line = n))), IrInstruction(instruction = CallKnown(5)("t" + Ashes.Text.fromInt(n))(6)(7)(8)(false), location = Some(Loc(filePath = "b" + Ashes.Text.fromInt(n), line = n))), IrInstruction(instruction = CallKnown(9)("u")(1)(2)(3)(false), location = Some(Loc(filePath = "c" + Ashes.Text.fromInt(n), line = n))), IrInstruction(instruction = Other(n), location = None)]
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
