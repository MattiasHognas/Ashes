// expect: 41
type Result(E, A) =
    | Ok(A)
    | Error(E)

type AppError =
    | Wrapped(String)

let x = Ok(41) |!> Wrapped
in 
    match x with
        | Ok(value) -> Ashes.IO.print(value)
        | Error(Wrapped(_)) -> Ashes.IO.print(0)
