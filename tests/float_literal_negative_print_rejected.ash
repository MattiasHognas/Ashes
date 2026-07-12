// A negated float literal is a valid Float (it folds into the literal rather than desugaring to
// `0 - 1.25`, which used to mis-resolve as Int). print is Int-only, so this is now rejected for that
// reason -- use Ashes.IO.write(Ashes.Text.fromFloat(...)) for floats.
// expect-compile-error: print() does not support type Float yet.
Ashes.IO.print(-1.25)
