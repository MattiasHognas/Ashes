// expect-compile-error: Constructor 'None' expects 0 argument(s) but got 1.
type Option =
    | None
    | Some(T)

None(1)
