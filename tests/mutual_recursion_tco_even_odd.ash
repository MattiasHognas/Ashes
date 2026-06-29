// expect: true
let rec isEven n = 
    match n with
        | 0 -> true
        | _ -> isOdd(n - 1)
and isOdd n = 
    match n with
        | 0 -> false
        | _ -> isEven(n - 1)

Ashes.IO.print(isEven(1000000))
