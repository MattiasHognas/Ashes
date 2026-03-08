let p = (1, (2, (3, 4)))
in 
    Ashes.IO.print(match p with
        | (a, (b, (c, d))) -> a + b + c + d)
