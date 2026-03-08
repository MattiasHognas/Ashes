// expect: fail
type Result(E, A) =
    | Ok(A)
    | Error(E)

type AppError =
    | Fail(String)

let x = 
    let? a = Error(Fail("fail"))
    in Ok(a)
in 
    match x with
        | Ok(_) -> Ashes.IO.print("ok")
        | Error(Fail(msg)) -> Ashes.IO.print(msg)
