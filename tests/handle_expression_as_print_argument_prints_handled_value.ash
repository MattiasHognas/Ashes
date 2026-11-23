// expect: handled
capability Store =
    | load : Unit -> Str needs {ConsoleIO}

let get : Unit -> Str needs {Store} =
    given (_) -> Store.load(Unit)

Ashes.IO.print(handle get(Unit) with
    | Store.load(_u) -> resume("handled")
    | return(r) -> r)
