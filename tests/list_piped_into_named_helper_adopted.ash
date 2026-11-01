// expect: 10000

type Reference =
    | referenceModule: Str
    | referenceId: Int

let recursive modulesOf (references: List(Reference)) =
    match references with
        | [] -> []
        | reference :: rest -> reference.referenceModule :: modulesOf(rest)

let recursive idsOf (references: List(Reference)) =
    match references with
        | [] -> []
        | reference :: rest -> reference.referenceId :: idsOf(rest)

let mkRefs (seed: Int) = [Reference(referenceModule = "Ashes." + "Text", referenceId = seed), Reference(referenceModule = "Ashes." + "Byte", referenceId = seed + 1)]

let recursive dropFirst (names: List(Str)) =
    match names with
        | [] -> []
        | name :: rest ->
            if name == "Ashes.Text"
            then dropFirst(rest)
            else name :: dropFirst(rest)

let recursive dropOne (ids: List(Int)) =
    match ids with
        | [] -> []
        | id :: rest ->
            if id == 1
            then dropOne(rest)
            else id :: dropOne(rest)

let piped (seed: Int) =
    seed
    |> mkRefs
    |> modulesOf
    |> dropFirst

let nested (seed: Int) =
    seed
    |> mkRefs
    |> modulesOf
    |> dropFirst

let pipedIds (seed: Int) =
    seed
    |> mkRefs
    |> idsOf
    |> dropOne

let recursive count (names: List(Str)) (total: Int) =
    match names with
        | [] -> total
        | _ :: rest -> count(rest)(total + 1)

let recursive countIds (ids: List(Int)) (total: Int) =
    match ids with
        | [] -> total
        | _ :: rest -> countIds(rest)(total + 1)

let run (seed: Int) =
    seed + count(piped(seed))(0) + count(nested(seed))(0) + countIds(pipedIds(seed))(0)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(1) + 1)

0
|> rounds(2000)
|> Ashes.IO.print
