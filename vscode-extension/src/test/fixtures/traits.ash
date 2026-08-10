trait Render(a) =
    | render : a -> Str

implement Render(Int) =
    | render =
        given (_) -> "int"

type Box(a) =
    | Box(a)
    deriving {Eq, Show, Hash}

let display : a -> Str requires {Render(a)} =
    given (value) -> Render.render(value)

display(1)
