// Flat top-level declarations: a `type`, two helper `let`s, then a trailing
// `let ... in` that runs the Result pipeline and matches on the outcome.

type ParseError =
    | NotAnInt(Str)

let trim x = 
    if x == " 42 "
    then "42"
    else x

let parse x = 
    if x == "42"
    then Ok(42)
    else Error(NotAnInt(x))

let x = 
    Ok(" 42 ")
    |?> trim
    |?> parse
    |?> (given (n) -> n + 1)
in 
    match x with
        | Ok(v) -> Ashes.IO.print(v)
        | Error(NotAnInt(_)) -> Ashes.IO.print(0)
