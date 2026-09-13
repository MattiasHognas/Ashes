// expect: compared
// The operand's type is only fixed by the |!> mapper's result, which the comparison is lowered before.
// A single pass reaching the comparison sees a type variable and cannot tell a deferred type from an
// ambiguous one, so the evidence for == is pinned to what lowering the whole program proves.
let generatedResult : Result(Str, Int) =
    let? bound = Error("compared")
    in
        (if bound == bound
        then Ok(0)
        else Ok(1)) |!> (given (mapped: Int) -> bound)
in
    match generatedResult with
        | Ok(_) -> Ashes.IO.print("mapped")
        | Error(message) -> Ashes.IO.print(message)
