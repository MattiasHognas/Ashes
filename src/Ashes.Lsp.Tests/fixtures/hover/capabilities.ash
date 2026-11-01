capability Clock =
    | now : Unit -> Int

capability Log =
    | log : Str -> Unit needs {ConsoleIO}

provide Log =
    | log =
        given (msg) -> Ashes.IO.print(msg)

let stamp : Unit -> Int needs {Clock} =
    given (_) -> perform Clock.now(Unit) + 1

let x =
    handle stamp(Unit) with
        | Clock.now(_) -> resume(42)
        | return(r) -> r * 2

let _ = Log.log("hi")

Ashes.IO.print(x)
