// expect: 43
let x = 
    let? n = Ok(42)
    in Ok(n + 1)
in 
    match x with
        | Ok(v) -> Ashes.IO.print(v)
        | Error(_) -> Ashes.IO.print(0)
