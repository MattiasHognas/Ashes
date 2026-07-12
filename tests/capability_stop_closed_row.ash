// expect-compile-error: not permitted by the closed row

capability Clock =
    | now : Unit -> Int

let bad : Int -> Int needs {Clock} =
    given (x) ->
        let _u = Stop.stop(Unit)
        in x

Ashes.IO.print("unreachable")
