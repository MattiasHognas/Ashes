// expect-compile-error: Non-exhaustive match expression. Missing case: x :: xs.
let xs = [1]
in 
    match xs with
        | [] -> 0
