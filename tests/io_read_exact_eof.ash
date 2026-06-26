// stdin: hi\n
// expect: eof
match Ashes.IO.readExact(10) with
    | Ok(_) -> Ashes.IO.print("ok")
    | Error(_) -> Ashes.IO.print("eof")
