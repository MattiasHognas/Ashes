// A loop walks a list of names and a list of types side by side by matching the tuple of both, and
// returns Some of the type under the name it finds. The reference the arena Some takes on the found
// type is settled by the return's copy alone, so each round's types are released.
// expect: 9000
type Ty =
    | TInt
    | TList(Ty)
    | TNamed(Str)

let recursive findField (fieldName: Str) (fieldNames: List(Str)) (fieldTypes: List(Ty)) =
    match (fieldNames, fieldTypes) with
        | (candidateName :: nameTail, fieldType :: typeTail) ->
            if fieldName == candidateName
            then Some(fieldType)
            else findField(fieldName)(nameTail)(typeTail)
        | _ -> None

let weigh (found: Maybe(Ty)) =
    match found with
        | None -> 0
        | Some(TInt) -> 1
        | Some(TList(_)) -> 2
        | Some(TNamed(_)) -> 3

let weighAll (name: Str) (names: List(Str)) (types: List(Ty)) =
    weigh(findField("value")(names)(types)) + weigh(findField(name)(names)(types)) + weigh(findField("owner")(names)(types)) + weigh(findField("absent")(names)(types))

let once (name: Str) = weighAll(name)(["value", name, "owner"])([TInt, TList(TInt), TNamed(name)])

let recursive rounds (n: Int) (sum: Int) =
    if n == 0
    then sum
    else rounds(n - 1)(sum + once("f" + Ashes.Text.fromInt(n)))

0
|> rounds(1500)
|> Ashes.IO.print
