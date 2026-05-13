// expect: Ashes.Text.parseInt() invalid input
match Ashes.Text.parseInt("12x") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(value) -> Ashes.IO.print(value)
