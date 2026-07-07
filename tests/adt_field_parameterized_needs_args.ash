// expect-compile-error: expects 1 type argument
type Inner =
    | Inner(a)

type Outer =
    | Outer(Inner)

Ashes.IO.print("unreachable")
