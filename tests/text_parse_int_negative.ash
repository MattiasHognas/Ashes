// expect: -42
match Ashes.Text.parseInt("-42") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(value) -> Ashes.IO.print(value)
