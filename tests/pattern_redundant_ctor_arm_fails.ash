// expect-compile-error: Unreachable match arm: constructor Red is already matched earlier.
type Color =
    | Red
    | Green
    | Blue

let c = Red
in 
    match c with
        | Red -> 1
        | Red -> 2
        | Green -> 3
        | Blue -> 4
