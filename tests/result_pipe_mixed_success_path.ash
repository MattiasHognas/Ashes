// expect: 42
type ParseError =
    | NotAnInt(Str)

type AppError =
    | Parse(ParseError)

let parse x = 
    if x == "41"
    then Ok(41)
    else Error(NotAnInt(x))
in 
    let y = 
        Ok("41")
        |?> parse
        |?> (given (n) -> n + 1)
        |!> Parse
    in 
        match y with
            | Ok(value) -> Ashes.IO.print(value)
            | Error(Parse(NotAnInt(_))) -> Ashes.IO.print(0)
