// expect: 1 1
trait Named(a) =
    | label : a -> Str

implement Named(Int) =
    | label =
        given (value) -> "int"

let describe x =
    (let _ = Named.label(x)
    in 1)

let counted x =
    (let recursive unused n = Named.label(x)
    in 1)

Ashes.IO.print(Ashes.Text.fromInt(describe(3)) + " " + Ashes.Text.fromInt(counted(4)))
