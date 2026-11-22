// expect-compile-error: Capability 'FileRead' is not permitted by the closed row needs {ConsoleIO}
capability Store =
    | load : Unit -> Str needs {ConsoleIO}

provide Store =
    | load =
        given (_) ->
            match Ashes.IO.File.readText("absent.txt") with
                | Ok(text) -> text
                | Error(_missing) -> "missing"

Unit
|> Store.load
|> Ashes.IO.print
