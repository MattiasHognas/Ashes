// expect: ok
match Ashes.Text.parseFloat("-0.25") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(value) -> 
        if value == 0.0 - 0.25
        then Ashes.IO.print("ok")
        else Ashes.IO.print("bad")
