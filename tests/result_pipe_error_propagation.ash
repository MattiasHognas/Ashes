// expect: bad
let fail x = Error("bad")
in 
    let parse x = Ok(42)
    in 
        let y = 
            Ok("42")
            |?> fail
            |?> parse
        in 
            match y with
                | Ok(_) -> Ashes.IO.print("ok")
                | Error(e) -> Ashes.IO.print(e)
