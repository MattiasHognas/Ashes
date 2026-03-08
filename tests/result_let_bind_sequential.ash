// expect: 5
type Result(E, A) =
    | Ok(A)
    | Error(E)

let x = 
    let? a = Ok(2)
    in 
        let? b = Ok(3)
        in Ok(a + b)
in 
    match x with
        | Ok(v) -> Ashes.IO.print(v)
        | Error(_) -> Ashes.IO.print(0)
