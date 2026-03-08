// expect: 6
let p = ((1, 2), 3)
in 
    Ashes.IO.print(match p with
        | ((a, b), c) -> a + b + c)
