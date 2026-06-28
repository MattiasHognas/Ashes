let rec loop _ = 
    match Ashes.IO.readLine(Unit) with
        | None -> Unit
        | Some(line) -> 
            let _ = Ashes.IO.writeLine(line)
            in loop(Unit)
in loop(Unit)
