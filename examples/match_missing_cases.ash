type Color =
    | Red
    | Green
    | Blue

let c = Red
in 
    Ashes.IO.print(match c with
        | Red -> 1
        | Green -> 2
        | Blue -> 3)
