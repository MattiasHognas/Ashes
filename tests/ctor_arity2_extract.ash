// expect: 3
type Pair =
    | Pair(Int, Int)

let p = Pair(1)(2)
in 
    match p with
        | Pair(a, b) -> Ashes.IO.print(a + b)
