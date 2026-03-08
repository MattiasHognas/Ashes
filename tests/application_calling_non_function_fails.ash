// expect-compile-error: Attempted to call 'x' with 1 argument(s), but its type is Int, not a function.
let x = 1
in Ashes.IO.print(x(1))
