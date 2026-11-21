import Labels
let render : a -> Str requires {Label(a)} =
    given (value) -> Label.text(value)
