// expect: Ashes.File.writeText() failed
match Ashes.File.writeText("")("nope") with
    | Ok(_) -> Ashes.IO.print("ok")
    | Error(msg) -> Ashes.IO.print(msg)
