match Ashes.File.readText("a.txt") with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(text) -> 
        match Ashes.File.writeText("b.txt")(text) with
            | Ok(_) -> Ashes.IO.print(1)
            | Error(msg) -> Ashes.IO.print(msg)
