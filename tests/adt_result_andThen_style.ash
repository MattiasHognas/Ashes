// expect: ok
type Result =
    | Ok(T)
    | Error(T)

let r1 = Ok(5)
in 
    let r2 = 
        match r1 with
            | Ok(x) -> Ok(x + 1)
            | Error(e) -> Error(e)
    in 
        match r2 with
            | Ok(_) -> Ashes.IO.print("ok")
            | Error(_) -> Ashes.IO.print("error")
