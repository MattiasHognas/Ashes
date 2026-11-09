// expect: 8
type Loc =
    | filePath: Str
    | line: Int

type Kind =
    | CallKnown(Int, Str, Int)
    | Other(Int)

type IrInstruction =
    | instruction: Kind
    | location: Maybe(Loc)

let recursive setAssociation key value entries =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setAssociation(key)(value)(tail)

let recursive collect remaining (nextTemp: Int) expansions (removed: List(Int)) rewrites =
    match remaining with
        | [] -> (expansions, removed, rewrites, nextTemp)
        | (position, irInst) :: tail ->
            match irInst with
                | IrInstruction { instruction = CallKnown(_, _, envTemp), location = loc } ->
                    collect(tail)(nextTemp + 1)(setAssociation(position)([IrInstruction(instruction = Other(envTemp), location = loc)])(expansions))(removed)(rewrites)
                | _ -> collect(tail)(nextTemp)(expansions)(removed)(rewrites)

let recursive indexInstructions (insts: List(IrInstruction)) (n: Int) acc =
    match insts with
        | [] -> Ashes.Collection.List.reverse(acc)
        | i :: rest -> indexInstructions(rest)(n + 1)((n, i) :: acc)

let probe (n: Int) =
    (let insts = [IrInstruction(instruction = CallKnown(1)("s" + Ashes.Text.fromInt(n))(2), location = Some(Loc(filePath = "a", line = n))), IrInstruction(instruction = CallKnown(5)("t")(6), location = Some(Loc(filePath = "b" + Ashes.Text.fromInt(n), line = n))), IrInstruction(instruction = Other(n), location = None)]
    in
        match collect(indexInstructions(insts)(0)([]))(0)([])([])([]) with
            | (expansions, _removed, _rewrites, nextTemp) -> Ashes.Collection.List.length(expansions) + nextTemp)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(2)
|> Ashes.IO.print
