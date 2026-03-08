// expect: 1
let last = 
    fun (xs) -> 
        match xs with
            | [] -> Ashes.IO.panic("empty")
            | x :: rest -> x
in Ashes.IO.print(last([1]))
