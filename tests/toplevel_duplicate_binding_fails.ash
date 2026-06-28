// expect-compile-error: Duplicate top-level binding
let x = 1

let x = 2

let y = x
in Ashes.IO.print(y)
