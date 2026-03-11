// file: file.txt = x
// expect: true
match Ashes.Fs.exists("file.txt") with
    | Ok(found) -> 
        if found
        then Ashes.IO.print("true")
        else Ashes.IO.print("false")
    | Error(msg) -> Ashes.IO.print(msg)
