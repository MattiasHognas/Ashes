// A finder with untyped parameters compares names with `==`, so it is constrained by Eq and its call
// goes to a copy lowered at the caller's types. The copy returns its Some owned, as a typed finder
// does, and the caller that matches the result releases it with the type it holds.
// expect: 4500
type Ty =
    | TInt
    | TList(Ty)
    | TNamed(Int, Str, List(Ty))

let findField fieldName fieldNames fieldTypes =
    match (fieldNames, fieldTypes) with
        | (candidateName :: nameTail, fieldType :: typeTail) ->
            if fieldName == candidateName
            then Some(fieldType)
            else None
        | _ -> None

let weigh (name: Str) (wanted: Str) =
    match findField(wanted)([name, "value"])([TNamed(1)(name)([TInt]), TList(TInt)]) with
        | None -> 0
        | Some(TNamed(_, _, _)) -> 3
        | Some(_fieldType) -> 1

let once (name: Str) = weigh(name)(name) + weigh(name)("value")

let recursive rounds (n: Int) (sum: Int) =
    if n == 0
    then sum
    else rounds(n - 1)(sum + once("f" + Ashes.Text.fromInt(n)))

0
|> rounds(1500)
|> Ashes.IO.print
