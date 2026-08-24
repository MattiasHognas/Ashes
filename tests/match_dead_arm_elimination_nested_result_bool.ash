// expect: 1,2,3
// Dead-arm elimination regression: a
// match with a redundant trailing wildcard AFTER already-exhaustive nested-pattern coverage
// (Ok(true) | Ok(false) | Error(_)) must trim only the truly unreachable wildcard, never mistake
// top-level-constructor-tag coverage (Ok/Error both "seen") for full coverage and silently drop a
// reachable nested arm like Ok(false). All three reachable arms must still produce correct output.
let classify res =
    match res with
        | Ok(true) -> 1
        | Ok(false) -> 2
        | Error(_) -> 3
        | _ -> 999

let a = classify(Ok(true))

let b = classify(Ok(false))

let c = classify(Error("boom"))
in
    Ashes.IO.print(
        Ashes.Text.fromInt(a) + "," + Ashes.Text.fromInt(b) + "," + Ashes.Text.fromInt(c)
    )
