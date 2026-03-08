// expect: 3
let p = (1, 2)
in 
    match p with
        | (a, b) -> Ashes.IO.print(a + b)
