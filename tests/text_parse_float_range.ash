// expect: Ashes.Text.parseFloat() out of range
match Ashes.Text.parseFloat("1e309") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(_) -> Ashes.IO.print("bad")
