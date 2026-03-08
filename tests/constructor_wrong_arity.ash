// expect-compile-error: Constructor 'Some' expects 1 argument(s) but got 2.
type Option =
    | None
    | Some(T)

Some(1)(2)
