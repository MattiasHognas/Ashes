type ParseError =
    | NotAnInt(String)

let trim = 
    fun (x) -> 
        if x == " 42 "
        then "42"
        else x
in 
    let parse = 
        fun (x) -> 
            if x == "42"
            then Ok(42)
            else Error(NotAnInt(x))
    in 
        let x = 
            Ok(" 42 ")
            |?> trim
            |?> parse
            |?> (fun (n) -> n + 1)
        in 
            match x with
                | Ok(v) -> Ashes.IO.print(v)
                | Error(NotAnInt(_)) -> Ashes.IO.print(0)
