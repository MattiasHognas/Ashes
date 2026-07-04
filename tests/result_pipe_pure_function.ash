// expect: 4
let x = 
    Ok(3) |?> (given (n) -> n + 1)
in 
    match x with
        | Ok(v) -> Ashes.IO.print(v)
        | Error(_) -> Ashes.IO.print(0)
