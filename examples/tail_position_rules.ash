let rec f x = 
    if x >= 10
    then 0
    else f(x + 1)
in Ashes.IO.print(f(0))
