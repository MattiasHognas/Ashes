// expect-compile-error: Constructor 'None' expects 0 argument(s) but pattern has 1.
type Maybe =
    | None
    | Some(T)

match None with
    | None(x) -> 0
    | Some(x) -> x
