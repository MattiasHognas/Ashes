// expect-compile-error: Named(Str)'. Requirement trace:
trait Named(a) =
    | label : a -> Str

implement Named(Int) =
    | label =
        given (value) -> "int"

let describe x =
    (let _ = Named.label(x)
    in 1)

"text"
|> describe
|> Ashes.IO.print
