capability Store =
    | load : Unit -> Str needs {FileRead}
    | save : Str -> Unit needs {ConsoleIO}

let describe =
    given (_) -> "stored " + Store.load(Unit)
