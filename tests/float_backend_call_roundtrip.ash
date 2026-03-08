// expect: 1
let id = 
    fun (x) -> x
in 
    if id(2.5) == 2.5
    then Ashes.IO.print(1)
    else Ashes.IO.print(0)
