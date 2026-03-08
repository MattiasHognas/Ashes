// expect: 3
let p = (1, 2)
in 
    Ashes.IO.print(match p with
        | (a, b) -> a + b)
