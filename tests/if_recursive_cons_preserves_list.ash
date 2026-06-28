// expect: 1234
let rec copy xs = 
    match xs with
        | [] -> []
        | head :: tail -> 
            if true
            then head :: copy(tail)
            else copy(tail)
in 
    let rec digits acc xs = 
        match xs with
            | [] -> acc
            | head :: tail -> digits(acc * 10 + head)(tail)
    in Ashes.IO.print(digits(0)(copy([1, 2, 3, 4])))
