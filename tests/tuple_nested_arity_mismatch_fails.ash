// expect-compile-error: Type mismatch: (Int, Int) vs (a, b, c).
let p = (1, (2, 3))
in 
    match p with
        | (a, (b, c, d)) -> 0
