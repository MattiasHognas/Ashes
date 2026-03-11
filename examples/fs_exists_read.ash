match Ashes.File.exists("input.txt") with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(found) -> 
        if found
        then 
            match Ashes.File.readText("input.txt") with
                | Ok(text) -> Ashes.IO.print(text)
                | Error(msg) -> Ashes.IO.print(msg)
        else Ashes.IO.print("missing")
