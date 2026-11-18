// A `let` that would generalize over the argument it prints is rejected: `print` takes an `Int`, an
// unsigned width below 64 bits, a `Str` or a `Bool`, and the argument's type is still open there.
// expect-compile-error: print() does not support type a yet.
let show value = Ashes.IO.print(value)

show("text")
