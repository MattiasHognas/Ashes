match Ashes.Fs.writeText("out.txt")("hello") with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(_) -> 
        match Ashes.Fs.readText("out.txt") with
            | Ok(text) -> Ashes.IO.print(text)
            | Error(msg) -> Ashes.IO.print(msg)
