// expect: 123
match Ashes.Text.parseInt("123") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(value) -> Ashes.IO.print(value)
