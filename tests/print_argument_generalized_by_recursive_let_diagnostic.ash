// A recursive binding that would generalize over the argument it prints is rejected as a single
// binding is.
// expect-compile-error: print() does not support type a yet.
let recursive repeat value count =
    if count == 0
    then Unit
    else
        let _ = Ashes.IO.print(value)
        in repeat(value)(count - 1)

repeat("text")(2)
