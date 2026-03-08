// expect: 42
let t = (42, 0)
in 
    Ashes.IO.print(match t with
        | (a, b) -> a)
