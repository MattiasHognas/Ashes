// expect: 12
let recursive setAssociation key value entries =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setAssociation(key)(value)(tail)

let recursive lookupAssociation key entries =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupAssociation(key)(tail)

let recursive addKnown (names: List(Str)) known changed =
    match names with
        | [] -> (known, changed)
        | name :: rest ->
            match lookupAssociation(name)(known) with
                | Some(_) -> addKnown(rest)(known)(changed)
                | None ->
                    addKnown(rest)(setAssociation(name)(name + "_next")(known))(true)

let recursive runKnown (names: List(Str)) known =
    match addKnown(names)(known)(false) with
        | (next, true) -> runKnown(names)(next)
        | (next, false) -> next

let computeKnown (names: List(Str)) known = runKnown(names)(known)

let devirt known (name: Str) =
    match lookupAssociation(name)(known) with
        | Some(label) -> Ashes.Text.byteLength(label)
        | None -> 0

let recursive total (xs: List(Int)) =
    match xs with
        | [] -> 0
        | x :: rest -> x + total(rest)

let recursive collectSites (names: List(Str)) (sites: List(Str)) (bad: List(Str)) =
    match names with
        | [] -> (sites, bad)
        | name :: rest -> collectSites(rest)(name :: sites)(bad)

let recursive computeCaptured (sites: List(Str)) acc (bad: List(Str)) known =
    match sites with
        | [] -> acc
        | site :: rest ->
            match lookupAssociation(site)(known) with
                | Some(label) -> computeCaptured(rest)((site, label) :: acc)(bad)(known)
                | None -> computeCaptured(rest)(acc)(bad)(known)

let recursive countPairs (xs: List((Str, Str))) =
    match xs with
        | [] -> 0
        | _ :: rest -> 1 + countPairs(rest)

let devirtCaptured (entry: Str) (names: List(Str)) =
    (let all = entry :: names
    in
        let knownReturned = computeKnown(all)([])
        in
            match collectSites(all)([])([]) with
                | (sites, unresolvable) ->
                    knownReturned
                    |> computeCaptured(sites)([])(unresolvable)
                    |> countPairs)

let probe (n: Int) = devirtCaptured("e" + Ashes.Text.fromInt(n))(["a", "b", "c"])

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(3)
|> Ashes.IO.print
