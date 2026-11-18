// Calling a value that is no function reports the first argument the chain applies, however many
// follow.
// expect-compile-error: Attempted to call 'x' with 1 argument(s), but its type is Int, not a function.
let x = 1

2
|> x(1)
|> Ashes.IO.print
