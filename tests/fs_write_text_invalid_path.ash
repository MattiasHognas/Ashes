// expect: Ashes.Fs.writeText() failed
match Ashes.Fs.writeText("")("nope") with
    | Ok(_) -> Ashes.IO.print("ok")
    | Error(msg) -> Ashes.IO.print(msg)
