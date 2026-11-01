capability Clock =
    | now : Unit -> Int

capability Log =
    | log : Str -> Unit needs {ConsoleIO}

provide Log =
    | log =
        given (msg) -> Ashes.IO.print(msg)

let stamp : Unit -> Int needs {Clock} =
    given (_) -> perform Clock.now(Unit) + 1

let tick a =
    (let t = Clock.now(Unit)
    in
        given (b) -> Clock.now(Unit) + t + a + b)

let x =
    handle stamp(Unit) with
        | Clock.now(_) -> resume(42)
        | return(r) -> r * 2

let _ = Log.log("hi")

Ashes.IO.print(x)
