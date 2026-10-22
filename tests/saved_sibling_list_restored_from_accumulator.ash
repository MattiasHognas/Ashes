// expect: 74
// A loop saves one parameter's list (`knownInts`) into another's association list and later restores
// it as the first parameter's successor, out of the association list it then shrinks. A list held
// inside an accumulator's elements can be handed on to an arena sibling parameter this way, so such
// an accumulator stays in the arena: placed on the reference-counted heap, the back edge would
// release the association list, restored list included, while the sibling still names it.
type Step =
    | Known(Int, Int)
    | Save(Str)
    | Restore(Str)
    | Other(Int)

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

let recursive setIntAssociation key value entries =
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

let recursive sumKnown (known: List((Int, Int))) (sum: Int) =
    match known with
        | [] -> sum
        | (_, v) :: tail -> sumKnown(tail)(sum + v)

let reduce steps =
    (let recursive reduceLoop insts knownInts savedInts acc =
        match insts with
            | [] -> acc + sumKnown(knownInts)(0)
            | step :: tail ->
                match step with
                    | Known(dest, v) ->
                        let nextInts = setIntAssociation(dest)(v)(knownInts)
                        in reduceLoop(tail)(nextInts)(savedInts)(acc + 1)
                    | Save(target) ->
                        let nextSaved = setAssociation(target)(knownInts)(savedInts)
                        in reduceLoop(tail)(knownInts)(nextSaved)(acc + 1)
                    | Restore(name) ->
                        let nextInts =
                            match lookupAssociation(name)(savedInts) with
                                | Some(si) -> si
                                | None -> []
                        in
                            reduceLoop(tail)(nextInts)(removeAssociation(name)(savedInts))(acc + 1)
                    | _ -> reduceLoop(tail)(knownInts)(savedInts)(acc)
    in reduceLoop(steps)([])([])(0))

let run n = reduce([Known(1)(n), Save("a" + Ashes.Text.fromInt(n)), Known(2)(3), Save("b"), Restore("a" + Ashes.Text.fromInt(n)), Known(3)(4), Restore("b"), Other(0), Known(4)(5)])

let recursive rounds n sum =
    if n == 0
    then sum
    else rounds(n - 1)(sum + run(n))

0
|> rounds(4)
|> Ashes.IO.print
