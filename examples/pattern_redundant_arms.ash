type Bool2 =
    | T
    | F

let b = T
in 
    Ashes.IO.print(match b with
        | T -> 1
        | F -> 0)
