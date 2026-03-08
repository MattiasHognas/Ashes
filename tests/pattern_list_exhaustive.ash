// expect: 1
let xs = [1, 2, 3]
in 
    Ashes.IO.print(match xs with
        | [] -> 0
        | x :: rest -> 1)
