capability Store =
    | load : Unit -> Str
    | save : Str -> Unit

let describe =
    given (_) -> "stored " + Store.load(Unit)
