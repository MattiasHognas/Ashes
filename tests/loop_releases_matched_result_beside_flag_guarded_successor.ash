// expect: 18
type Case =
    | tag: Int
    | label: Str

type Kind =
    | KSwitch(Int, List(Case))
    | KLoad(Int, Int)
    | KMake(Int, Str, List(Int))
    | KCall(Str, List(Int))
    | KOther

let recursive lookupAssociation key entries =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupAssociation(key)(tail)

let resolve (defs: List((Int, Kind))) (temp: Int) =
    match lookupAssociation(temp)(defs) with
        | None -> None
        | Some(def) ->
            match def with
                | KLoad(_, slot) ->
                    match lookupAssociation(slot)(defs) with
                        | Some(stored) -> Some((stored, true))
                        | None -> Some((def, false))
                | _ -> Some((def, false))

let tryBuild (definition: Kind) =
    match definition with
        | KMake(_, label, args) ->
            args
            |> KCall(label)
            |> Some
        | _ -> None

let recursive count (xs: List(Int)) (n: Int) =
    match xs with
        | [] -> n
        | _ :: rest -> count(rest)(n + 1)

let recursive count2 (kinds: List(Kind)) (n: Int) =
    match kinds with
        | [] -> n
        | _ :: rest -> count2(rest)(n + 1)

let recursive passFirst (defs: List((Int, Kind))) (temps: List(Int)) (dead: List(Int)) (acc: List(Kind)) =
    match temps with
        | [] -> count(dead)(0) + count2(acc)(0)
        | temp :: rest ->
            match resolve(defs)(temp) with
                | Some((definition, through)) ->
                    match tryBuild(definition) with
                        | Some(known) ->
                            let nextDead =
                                if through
                                then temp :: dead
                                else dead
                            in passFirst(defs)(rest)(nextDead)(known :: acc)
                        | None -> passFirst(defs)(rest)(dead)(acc)
                | None -> passFirst(defs)(rest)(dead)(acc)

let recursive pass (defs: List((Int, Kind))) (temps: List(Int)) (dead: List(Int)) (acc: List(Kind)) =
    match temps with
        | [] -> count(dead)(0) + count2(acc)(0)
        | temp :: rest ->
            match resolve(defs)(temp) with
                | Some((definition, through)) ->
                    match tryBuild(definition) with
                        | Some(known) ->
                            let nextDead =
                                if through
                                then temp :: dead
                                else dead
                            in pass(defs)(rest)(nextDead)(known :: acc)
                        | None -> pass(defs)(rest)(dead)(acc)
                | None -> pass(defs)(rest)(dead)(acc)

let probe (n: Int) =
    (let defs =
        [(1, KLoad(1)(5)), (5, KMake(n)(Ashes.Text.fromInt(n))([n, 1])), (3, KOther), (7, KSwitch(n)([Case(tag = 1, label = "a")])), (6, KMake(2)(Ashes.Text.fromInt(n + 1))([n]))]
    in passFirst(defs)([1, 3, 4, 6, 7])([])([]) + pass(defs)([1, 3, 4, 6, 7])([])([]))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(3)
|> Ashes.IO.print
