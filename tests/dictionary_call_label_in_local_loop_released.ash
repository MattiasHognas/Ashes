// expect: 18
// A local loop hands each jump's label to an Eq-constrained association update. The reference the
// call keeps for its arena result is saved aside each iteration and released at the back edge.
type Op =
    | Jump(Str)
    | Label(Str)
    | Const(Int, Int)
    | Other(Int)

type Inst =
    | instruction: Op
    | location: Int

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

let recursive setIntAssociation (key: Int) (value: Int) (entries: List((Int, Int))) =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setIntAssociation(key)(value)(tail)

let recursive removeAssociation key entries =
    match entries with
        | [] -> []
        | (k, v) :: tail ->
            if k == key
            then tail
            else (k, v) :: removeAssociation(key)(tail)

let reduce (refs: List((Str, Int))) (instructions: List(Inst)) =
    (let recursive reduceLoop insts knownInts savedInts prevTerm acc =
        match insts with
            | [] -> Ashes.Collection.List.reverse(acc)
            | (Inst { instruction = inst } as irInst) :: tail ->
                match inst with
                    | Const(dest, v) ->
                        let nextInts = setIntAssociation(dest)(v)(knownInts)
                        in reduceLoop(tail)(nextInts)(savedInts)(false)(irInst :: acc)
                    | Label(name) ->
                        let nextInts =
                            if prevTerm && lookupAssociation(name)(refs) == Some(1)
                            then
                                match lookupAssociation(name)(savedInts) with
                                    | Some(si) -> si
                                    | None -> []
                            else knownInts
                        in
                            reduceLoop(tail)(nextInts)(removeAssociation(name)(savedInts))(false)(irInst :: acc)
                    | Jump(target) ->
                        let nextSaved = setAssociation(target)(knownInts)(savedInts)
                        in reduceLoop(tail)(knownInts)(nextSaved)(true)(irInst :: acc)
                    | _ -> reduceLoop(tail)(knownInts)(savedInts)(false)(irInst :: acc)
    in reduceLoop(instructions)([])([])(false)([]))

let label (n: Int) = "rcdrop_list_end_" + Ashes.Text.fromInt(n)

let recursive jumps (n: Int) (k: Int) (acc: List(Inst)) =
    if k == 0
    then
        Inst(instruction = n
        |> label
        |> Label, location = 0) :: acc
    else
        jumps(n)(k - 1)(Inst(instruction = n + k % 2
        |> label
        |> Jump, location = k) :: acc)

let program (n: Int) = jumps(n)(4)([Inst(instruction = Other(1), location = 9)])

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        n
        |> program
        |> reduce([(label(n), 1)])
        |> Ashes.Collection.List.length
        |> (given (size) -> loop(n - 1)(acc + size))

0
|> loop(3)
|> Ashes.Text.fromInt
|> Ashes.IO.print
