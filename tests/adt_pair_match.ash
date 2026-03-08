// expect: matched
type Pair =
    | Pair(A, B)

let p = Pair(1)(2)
in 
    match p with
        | Pair(_, _) -> Ashes.IO.print("matched")
