// expect: 43
let parse = 
    fun (text) -> 
        if text == "42"
        then Ok(42)
        else Error("bad")
in 
    let x = 
        let? a = Ok("42") |?> parse
        in Ok(a + 1)
    in 
        match x with
            | Ok(v) -> Ashes.IO.print(v)
            | Error(_) -> Ashes.IO.print(0)
