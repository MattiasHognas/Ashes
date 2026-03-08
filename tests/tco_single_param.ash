// expect: 10
let rec loop = 
    fun (i) -> 
        if i >= 10
        then i
        else loop(i + 1)
in Ashes.IO.print(loop(0))
