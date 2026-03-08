// expect: missing age
type Result(E, A) =
    | Ok(A)
    | Error(E)

type JsonError =
    | MissingField(String)

type AppError =
    | Json(JsonError)

let x = Error(Json(MissingField("age")))
in 
    match x with
        | Ok(_) -> Ashes.IO.print("ok")
        | Error(Json(MissingField(name))) -> Ashes.IO.print("missing " + name)
