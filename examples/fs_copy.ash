match Ashes.Fs.readText("a.txt") with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(text) -> 
        match Ashes.Fs.writeText("b.txt")(text) with
            | Ok(_) -> Ashes.IO.print("ok")
            | Error(msg) -> Ashes.IO.print(msg)
