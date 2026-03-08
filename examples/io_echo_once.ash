match Ashes.IO.readLine(Unit) with
    | None -> Unit
    | Some(line) -> Ashes.IO.writeLine(line)
