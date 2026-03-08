// stdin:
// expect: eof
match Ashes.IO.readLine(Unit) with
    | None -> Ashes.IO.print("eof")
    | Some(text) -> Ashes.IO.print(text)
