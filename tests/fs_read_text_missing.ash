// expect: Ashes.File.readText() failed
match Ashes.File.readText("does_not_exist.txt") with
    | Ok(text) -> Ashes.IO.print(text)
    | Error(msg) -> Ashes.IO.print(msg)
