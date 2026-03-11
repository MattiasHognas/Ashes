// expect: wrapped
type AppError =
    | Wrapped(String)

let x = Error("wrapped") |!> Wrapped
in 
    match x with
        | Ok(_) -> Ashes.IO.print("ok")
        | Error(Wrapped(msg)) -> Ashes.IO.print(msg)
