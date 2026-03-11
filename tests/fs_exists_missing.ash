// expect: false
match Ashes.Fs.exists("missing.txt") with
    | Ok(found) -> 
        if found
        then Ashes.IO.print("true")
        else Ashes.IO.print("false")
    | Error(msg) -> Ashes.IO.print(msg)
