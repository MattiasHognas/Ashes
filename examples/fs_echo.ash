match Ashes.Fs.readText("input.txt") with
    | Ok(text) -> Ashes.IO.print(text)
    | Error(msg) -> Ashes.IO.print(msg)
