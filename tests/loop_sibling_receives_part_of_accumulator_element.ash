// expect: 133
// reduceLoop hands knownInts an inner list matched out of a savedInts element (`Some(si) -> si`)
// while savedInts, an accumulator of `(Str, List((Int, Int)))`, is rebuilt by calls. Kept in the
// arena, knownInts would hold that list without a reference of its own after the back edge releases
// savedInts, so savedInts stays in the arena with it.
type Inst =
    | ILabel(Str)
    | IJump(Str)
    | IJumpIf(Str)
    | IConst(Int, Int)
    | INop

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

let recursive removeAssociation key entries =
    match entries with
        | [] -> []
        | (k, v) :: tail ->
            if k == key
            then tail
            else (k, v) :: removeAssociation(key)(tail)

let reduce (instructions: List(Inst)) =
    (let recursive reduceLoop insts knownInts savedInts prevTerm acc =
        match insts with
            | [] -> Ashes.Collection.List.length(acc) + Ashes.Collection.List.length(savedInts)
            | inst :: tail ->
                match inst with
                    | IConst(dest, value) ->
                        let nextInts = setAssociation(dest)(value)(knownInts)
                        in reduceLoop(tail)(nextInts)(savedInts)(false)(inst :: acc)
                    | ILabel(name) ->
                        let branchCount = Ashes.Text.byteLength(name) - 1
                        in
                            let nextInts =
                                if branchCount <= 1
                                then
                                    if prevTerm
                                    then
                                        match lookupAssociation(name)(savedInts) with
                                            | Some(si) -> si
                                            | None -> []
                                    else
                                        if branchCount == 0
                                        then knownInts
                                        else []
                                else []
                            in
                                reduceLoop(tail)(nextInts)(removeAssociation(name)(savedInts))(false)(inst :: acc)
                    | IJump(target) ->
                        let nextSaved = setAssociation(target)(knownInts)(savedInts)
                        in reduceLoop(tail)(knownInts)(nextSaved)(true)(inst :: acc)
                    | IJumpIf(target) ->
                        let nextSaved = setAssociation(target)(knownInts)(savedInts)
                        in reduceLoop(tail)(knownInts)(nextSaved)(false)(inst :: acc)
                    | INop -> reduceLoop(tail)(knownInts)(savedInts)(false)(inst :: acc)
    in reduceLoop(instructions)([])([])(false)([]))

let program (n: Int) = [IConst(1)(n), IJumpIf("a" + Ashes.Text.fromInt(n % 3)), IConst(2)(5), IJump("b"), ILabel("a0"), IConst(3)(1), IJumpIf("b"), IJump("c"), ILabel("b"), INop, IJump("a1"), ILabel("c"), ILabel("a1")]

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        loop(n - 1)(acc + reduce(program(n)))
in
    0
    |> loop(10)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
