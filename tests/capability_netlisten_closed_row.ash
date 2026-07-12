// expect-compile-error: not permitted by the closed row

capability Clock =
    | now : Unit -> Int

let bad : Int -> Int needs {Clock} =
    given (port) ->
        let _t = Ashes.Net.Tcp.Server.listen(port)
        in 42

Ashes.IO.print("unreachable")
