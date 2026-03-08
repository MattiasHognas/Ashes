// expect: 6
let rec sum = 
    fun (xs) -> 
        match xs with
            | [] -> 0
            | x :: rest -> x + sum(rest)
in Ashes.IO.print(sum([1, 2, 3]))
