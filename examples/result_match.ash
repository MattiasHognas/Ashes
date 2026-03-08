type Result(E, A) =
    | Ok(A)
    | Error(E)

let r = Ok(42)
in 
    match r with
        | Ok(_) -> Ashes.IO.print("ok")
        | Error(_) -> Ashes.IO.print("error")
