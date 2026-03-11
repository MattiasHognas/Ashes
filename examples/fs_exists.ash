match Ashes.File.exists("out.txt") with
    | Ok(found) -> 
        if found
        then Ashes.IO.print(1)
        else Ashes.IO.print(0)
    | Error(msg) -> Ashes.IO.print(msg)
