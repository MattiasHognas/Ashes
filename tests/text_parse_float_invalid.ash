// expect: Ashes.Text.parseFloat() invalid input
match Ashes.Text.parseFloat("1.") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(_) -> Ashes.IO.print("bad")
