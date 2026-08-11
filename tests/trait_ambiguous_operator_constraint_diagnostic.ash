// expect-compile-error: is ambiguous: its type cannot be determined from context
import Ashes.Trait
let f x = x
in
    Ashes.IO.print(if f == f
    then "y"
    else "n")
