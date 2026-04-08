// expect: big
let x = 20
in 
    Ashes.IO.print(match x with
        | n when n >= 11 -> "big"
        | _ -> "small")
