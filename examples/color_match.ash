type Color =
    | Red
    | Green
    | Blue

let c = Blue
in 
    Ashes.IO.print(match c with
        | Red -> 1
        | Blue -> 2
        | _ -> 0)
