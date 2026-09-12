// expect: mapped 8 recovered boom
// exit: 0

let logged =
    Ok(7) |?> (given (value: Int) -> Ok(value + 1))

let recovered =
    Error("boom") |!> (given (message: Str) -> message)

let report =
    match (logged, recovered) with
        | (Ok(value), Error(message)) -> "mapped " + Ashes.Text.fromInt(value) + " recovered " + message
        | _ -> "unexpected"
in
    Ok(report)
    |?> (given (line: Str) ->
        let printed = Ashes.IO.print(line)
        in Ok(line))
    |!> (given (failure: Str) ->
        let printed = Ashes.IO.print("unreachable")
        in failure)
