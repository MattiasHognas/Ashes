// expect: 1
let rec accumulate = 
    fun (n) -> 
        if n == 0
        then 0.0
        else accumulate(n - 1)
in 
    if accumulate(4) == 0.0
    then Ashes.IO.print(1)
    else Ashes.IO.print(0)
