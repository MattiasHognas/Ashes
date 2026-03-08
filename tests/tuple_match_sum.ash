// expect: 6
let t = (1, 2, 3)
in 
    Ashes.IO.print(match t with
        | (a, b, c) -> a + b + c)
