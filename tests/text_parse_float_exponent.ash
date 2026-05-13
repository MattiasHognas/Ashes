// expect: ok
match Ashes.Text.parseFloat("1e3") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(value) -> 
        if value == 1000.0
        then Ashes.IO.print("ok")
        else Ashes.IO.print("bad")
