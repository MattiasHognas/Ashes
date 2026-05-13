// expect: Ashes.Text.parseInt() overflow
match Ashes.Text.parseInt("9223372036854775808") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(value) -> Ashes.IO.print(value)
