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

let sameBox : Box(Int) -> Box(Int) -> Bool =
    given (left) -> given (right) -> Eq.equal(left)(right)

display(1)
