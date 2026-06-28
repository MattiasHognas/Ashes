// expect: true
let rec isEven = 
    fun (n) -> 
        if n == 0
        then true
        else isOdd(n - 1)
and isOdd = 
    fun (n) -> 
        if n == 0
        then false
        else isEven(n - 1)

let result = isEven(10)
in Ashes.IO.print(result)
