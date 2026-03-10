match Ashes.Fs.exists("out.txt") with
    | Ok(found) -> 
        if found
        then Ashes.IO.print("exists")
        else Ashes.IO.print("missing")
    | Error(msg) -> Ashes.IO.print(msg)
