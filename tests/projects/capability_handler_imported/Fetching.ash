capability Source =
    | read : Unit -> Str

let fetch : Unit -> Str needs {Source} =
    given (_) -> Source.read(Unit) + "!"
