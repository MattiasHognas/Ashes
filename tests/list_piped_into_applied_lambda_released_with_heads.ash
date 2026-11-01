// expect: 4000

type Reference =
    | referenceModule: Str
    | referenceMember: Str

let recursive containsText (value: Str) (values: List(Str)) =
    match values with
        | [] -> false
        | candidate :: rest ->
            if candidate == value
            then true
            else containsText(value)(rest)

let recursive reverseNames (names: List(Str)) (result: List(Str)) =
    match names with
        | [] -> result
        | name :: rest -> reverseNames(rest)(name :: result)

let recursive dedupe (names: List(Str)) (seen: List(Str)) =
    match names with
        | [] -> reverseNames(seen)([])
        | name :: rest ->
            if containsText(name)(seen)
            then dedupe(rest)(seen)
            else dedupe(rest)(name :: seen)

let recursive modulesOf (references: List(Reference)) =
    match references with
        | [] -> []
        | reference :: rest -> reference.referenceModule :: modulesOf(rest)

let mkRefs (seed: Int) = [Reference(referenceModule = "Ashes." + "Text", referenceMember = "jo" + "in"), Reference(referenceModule = "Ashes." + "Byte", referenceMember = "len" + "gth")]

let recursive dropFirst (names: List(Str)) =
    match names with
        | [] -> []
        | name :: rest ->
            if name == "Ashes.Text"
            then dropFirst(rest)
            else name :: dropFirst(rest)

let needed (seed: Int) =
    seed
    |> mkRefs
    |> modulesOf
    |> (given (names) -> dropFirst(names))

let run (seed: Int) =
    seed + Ashes.Collection.List.length(needed(seed))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(1))

0
|> rounds(2000)
|> Ashes.IO.print
