match Ashes.Fs.exists("file.txt") with
    | Ok(found) -> Ashes.IO.print(found)
    | Error(msg) -> Ashes.IO.print(msg)
