// expect: 4
type Result(E, A) =
    | Ok(A)
    | Error(E)

let x = 
    Ok(3) |?> (fun (n) -> n + 1)
in 
    match x with
        | Ok(v) -> Ashes.IO.print(v)
        | Error(_) -> Ashes.IO.print(0)
