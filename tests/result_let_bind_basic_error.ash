// expect: bad
let x = 
    let? n = Error("bad")
    in Ok(n + 1)
in 
    match x with
        | Ok(_) -> Ashes.IO.print("ok")
        | Error(e) -> Ashes.IO.print(e)
