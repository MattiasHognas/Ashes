// expect-compile-error: Tuple pattern arity mismatch: expected 2 element(s) but got 3.
let p = (1, 2)
in 
    match p with
        | (a, b, c) -> 0
