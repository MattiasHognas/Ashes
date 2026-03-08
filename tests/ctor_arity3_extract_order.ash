// expect: 123
type Triple =
    | Triple(Int, Int, Int)

let t = Triple(1)(2)(3)
in 
    match t with
        | Triple(a, b, c) -> 
            Ashes.IO.print(if a == 1
            then 
                if b == 2
                then 
                    if c == 3
                    then 123
                    else 0
                else 0
            else 0)
