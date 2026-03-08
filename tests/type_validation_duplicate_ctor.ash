// expect-compile-error: Duplicate constructor name 'True' in type 'Bool'.
type Bool =
    | True
    | True

Ashes.IO.print("ok")
