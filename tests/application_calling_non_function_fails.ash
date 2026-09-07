// expect-compile-error: Attempted to call 'x' with 1 argument(s), but its type is Int, not a function.
let x = 1
in
    1
    |> x
    |> Ashes.IO.print
