// expect: Ashes.Number.UInt.fromInt64 expects Int|1ZZZZZZZZZZZZZZZZZZZZZZZZZZ|Ashes.Number.UInt.fromInt64 expects Int|2ZZZZZZZZZZZZZZZZZZZZZZZZZZ
// A string bound out of a fresh `Some` scrutinee and stored inside an updated record field stays
// alive after the arm releases the scrutinee: the update's field takes its own reference.
type Failure =
    | Mismatch(Str)
    | Unknown

type Outcome =
    | code: Int
    | error: Maybe(Failure)

type Callee =
    | Plain(Str)
    | Qualified(Str, Str)
    | Located(Int, Callee)

let recursive calledName callee =
    match callee with
        | Located(_offset, inner) -> calledName(inner)
        | Plain(name) -> Some(name)
        | Qualified(moduleName, member) -> Some(moduleName + "." + member)

let argumentFailure callee (failure: Outcome) =
    match calledName(callee) with
        | Some(name) -> failure with error = Some(Mismatch(name))
        | None -> failure

let describe (result: Outcome) =
    match result.error with
        | Some(Mismatch(name)) -> name + " expects Int"
        | Some(Unknown) -> "unknown"
        | None -> "none"

let recursive run (count: Int) (acc: List(Str)) =
    if count <= 0
    then acc
    else
        let result =
            argumentFailure("fromInt64"
            |> Qualified("Ashes.Number." + "UInt")
            |> Located(count))(Outcome(code = count, error = None))
        in
            let filler = Ashes.Text.fromInt(count) + "ZZZZZZZZZZZZZZZZZZZZZZZZZZ"
            in run(count - 1)(describe(result) :: filler :: acc)

[]
|> run(2)
|> Ashes.Text.join("|")
|> Ashes.IO.print
