// expect-compile-error: ASH036
trait Label(a) =
    | text : a -> Str

let render : a -> Str requires {Label(a)} =
    given (value) -> Label.text(value)

let invoke (callback: Int -> Str) = callback(42)

render
|> invoke
|> Ashes.IO.print
