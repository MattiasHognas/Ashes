// expect-compile-error: Non-exhaustive match expression. Missing case: [].
let xs = []
in 
    match xs with
        | x :: rest -> 1
