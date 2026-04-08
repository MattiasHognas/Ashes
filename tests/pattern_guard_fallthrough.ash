// expect: small
let x = 5
in 
    Ashes.IO.print(match x with
        | n when n >= 11 -> "big"
        | _ -> "small")
