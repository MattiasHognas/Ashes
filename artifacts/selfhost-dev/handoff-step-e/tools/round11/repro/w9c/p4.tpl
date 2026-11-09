type Kind =
    | Dup(Int, Int, Bool)
    | Share(Int, Int)
    | Const(Int, Int)
    | Other(Int)

type Inst =
    | instruction: Kind
    | location: Maybe(Int)

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

let recursive resolveTemp remap temp =
    match lookupAssociation(temp)(remap) with
        | None -> temp
        | Some(resolved) -> resolveTemp(remap)(resolved)

let recursive listContains item xs =
    match xs with
        | [] -> false
        | head :: tail ->
            if head == item
            then true
            else listContains(item)(tail)

let definedOf inst =
    match inst with
        | Dup(a, _, _) -> [a]
        | Share(a, _) -> [a]
        | Const(a, _) -> [a]
        | Other(_) -> []

let mapTemps f inst =
    match inst with
        | Dup(a, b, c) -> Dup(f(a), f(b), c)
        | Share(a, b) -> Share(f(a), f(b))
        | Const(a, b) -> Const(f(a), b)
        | Other(a) -> Other(f(a))

let remapSourceTemps inst (remap: List((Int, Int))) =
    (let defined = definedOf(inst)
    in
        mapTemps(given (temp) ->
            if listContains(temp)(defined)
            then temp
            else resolveTemp(remap)(temp))(inst))

let recursive collectCopy (instructions: List(Inst)) acc =
    match instructions with
        | [] -> acc
        | Inst { instruction = inst } :: tail ->
            match inst with
                | Const(t, _) -> collectCopy(tail)(t :: acc)
                | _ -> collectCopy(tail)(acc)

let elide (instructions: List(Inst)) =
    (let copyTypes = collectCopy(instructions)([])
    in
        let useCounts = [(1, 2), (4, 1)]
        in
            let recursive buildRemap insts remap =
                match insts with
                    | [] -> remap
                    | Inst { instruction = inst } :: tail ->
                        match inst with
                            | Dup(dest, src, false) ->
                                let resolvedSrc = resolveTemp(remap)(src)
                                in
                                    remap
                                    |> setAssociation(dest)(resolvedSrc)
                                    |> buildRemap(tail)
                            | Share(dest, src) ->
                                let resolvedSrc = resolveTemp(remap)(src)
                                in
                                    let isCopy = listContains(resolvedSrc)(copyTypes)
                                    in
                                        let isSingleUse =
                                            match lookupAssociation(dest)(useCounts) with
                                                | Some(c) -> c <= 1
                                                | None -> true
                                        in
                                            let isEligible =
                                                if isCopy
                                                then true
                                                else isSingleUse
                                            in
                                                if isEligible
                                                then
                                                    remap
                                                    |> setAssociation(dest)(resolvedSrc)
                                                    |> buildRemap(tail)
                                                else buildRemap(tail)(remap)
                            | _ -> buildRemap(tail)(remap)
            in
                let step = buildRemap(instructions)
                in
                let remap = step([])
                in
                    let recursive applyRemap insts acc =
                        match insts with
                            | [] -> Ashes.Collection.List.reverse(acc)
                            | (Inst { instruction = inst, location = loc } as irInst) :: tail ->
                                match inst with
                                    | Dup(dest, _, false) ->
                                        if lookupAssociation(dest)(remap) != None
                                        then applyRemap(tail)(acc)
                                        else applyRemap(tail)(irInst :: acc)
                                    | Share(dest, _) ->
                                        if lookupAssociation(dest)(remap) != None
                                        then applyRemap(tail)(acc)
                                        else applyRemap(tail)(irInst :: acc)
                                    | _ ->
                                        let remapped = Inst(instruction = remapSourceTemps(inst)(remap), location = loc)
                                        in applyRemap(tail)(remapped :: acc)
                    in applyRemap(instructions)([]))

let probe (n: Int) = Ashes.Collection.List.length([Inst(instruction = Dup(3, 2, false), location = Some(n)), Inst(instruction = Share(6, 9), location = None), Inst(instruction = Other(3), location = None), Inst(instruction = Other(n), location = None)]
|> elide
|> elide)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

Ashes.IO.print(loop(ROUNDS)(0))
