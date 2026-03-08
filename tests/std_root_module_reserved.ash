// expect-compile-error: built-in runtime types are reserved
type Ashes =
    | X

Ashes.IO.print(1)
