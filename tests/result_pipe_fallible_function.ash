// expect: 42
type Result(E, A) =
    | Ok(A)
    | Error(E)

let parse = 
    fun (x) -> 
        if x == "42"
        then Ok(42)
        else Error("bad")
in 
    let y = Ok("42") |?> parse
    in 
        match y with
            | Ok(v) -> Ashes.IO.print(v)
            | Error(_) -> Ashes.IO.print(0)
