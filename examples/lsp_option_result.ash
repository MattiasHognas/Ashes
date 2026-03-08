type Option =
    | None
    | Some(T)

type Result(E, A) =
    | Ok(A)
    | Error(E)

let unwrapOr = 
    fun (opt) -> 
        fun (def) -> 
            match opt with
                | None -> def
                | Some(x) -> x
in 
    let r = Ok(42)
    in 
        match r with
            | Ok(v) -> Ashes.IO.print(unwrapOr(Some(v))(0))
            | Error(_) -> Ashes.IO.print(0)
