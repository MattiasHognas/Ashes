// expect: 3
let rec inc = 
    fun (x) -> x + 1
in Ashes.IO.print(inc(2))
