// expect-compile-error: Constructor 'Some' expects 1 argument(s) but pattern has 2. Expected shape: Some(T).
match Some(1) with
    | Some(x, y) -> x
    | None -> 0
