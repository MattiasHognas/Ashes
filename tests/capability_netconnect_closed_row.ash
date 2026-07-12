// expect-compile-error: not permitted by the closed row

capability Clock =
    | now : Unit -> Int

let bad : Str -> Int needs {Clock} =
    given (url) ->
        let _t = Ashes.Http.get(url)
        in 42

Ashes.IO.print("unreachable")
