match Ashes.Fs.exists("file.txt") with
    | Ok(found) -> 
        Ashes.IO.print(if found
        then "yes"
        else "no")
    | Error(msg) -> Ashes.IO.print(msg)
