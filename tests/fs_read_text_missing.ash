// expect: Ashes.Fs.readText() failed
match Ashes.Fs.readText("does_not_exist.txt") with
    | Ok(text) -> Ashes.IO.print(text)
    | Error(msg) -> Ashes.IO.print(msg)
