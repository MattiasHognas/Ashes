// expect: not-an-int
type ParseError =
    | NotAnInt(String)

type AppError =
    | Parse(ParseError)

let parse = 
    fun (x) -> Error(NotAnInt("not-an-int"))
in 
    let y = 
        Ok("abc")
        |?> parse
        |!> Parse
    in 
        match y with
            | Ok(_) -> Ashes.IO.print("ok")
            | Error(Parse(NotAnInt(text))) -> Ashes.IO.print(text)
