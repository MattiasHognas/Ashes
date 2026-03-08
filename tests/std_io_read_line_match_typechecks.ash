// stdin: anything\n
// expect: ok
match Ashes.IO.readLine(Unit) with
    | None -> Ashes.IO.writeLine("ok")
    | Some(_) -> Ashes.IO.writeLine("ok")
