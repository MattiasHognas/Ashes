// expect: ok
match Ashes.Text.parseFloat("1.5") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(value) -> 
        if value == 1.5
        then Ashes.IO.print("ok")
        else Ashes.IO.print("bad")
