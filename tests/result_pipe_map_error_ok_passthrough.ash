// expect: 41
type AppError =
    | Wrapped(String)

let x = Ok(41) |!> Wrapped
in 
    match x with
        | Ok(value) -> Ashes.IO.print(value)
        | Error(Wrapped(_)) -> Ashes.IO.print(0)
