// expect: 42
type Result(E, A) =
    | Ok(A)
    | Error(E)

type ParseError =
    | NotAnInt(String)

type AppError =
    | Parse(ParseError)

let parse = 
    fun (x) -> 
        if x == "41"
        then Ok(41)
        else Error(NotAnInt(x))
in 
    let y = 
        Ok("41")
        |?> parse
        |?> (fun (n) -> n + 1)
        |!> Parse
    in 
        match y with
            | Ok(value) -> Ashes.IO.print(value)
            | Error(Parse(NotAnInt(_))) -> Ashes.IO.print(0)
