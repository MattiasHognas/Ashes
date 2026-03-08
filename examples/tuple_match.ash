let p = ("a", "b")
in 
    Ashes.IO.print(match p with
        | (x, y) -> x + y)
