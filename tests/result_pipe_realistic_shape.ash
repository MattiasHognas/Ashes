// expect: 43
type ParseError =
    | NotAnInt(Str)

let trim x = 
    if x == " 42 "
    then "42"
    else x
in 
    let parse x = 
        if x == "42"
        then Ok(42)
        else Error(NotAnInt(x))
    in 
        let x = 
            Ok(" 42 ")
            |?> trim
            |?> parse
            |?> (given (n) -> n + 1)
        in 
            match x with
                | Ok(v) -> Ashes.IO.print(v)
                | Error(_) -> Ashes.IO.print(0)
