// expect-compile-error: Non-exhaustive match expression. Missing constructor(s): 'Green', 'Blue'.
type Color =
    | Red
    | Green
    | Blue

let c = Green
in 
    match c with
        | Red -> 1
