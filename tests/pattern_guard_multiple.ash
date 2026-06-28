// expect: medium
let classify x = 
    match x with
        | n when n >= 101 -> "big"
        | n when n >= 11 -> "medium"
        | _ -> "small"
in Ashes.IO.print(classify(50))
