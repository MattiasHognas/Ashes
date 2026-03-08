let _ = Ashes.IO.writeLine("Name?")
in 
    match Ashes.IO.readLine(Unit) with
        | None -> Ashes.IO.writeLine("No input")
        | Some(name) -> Ashes.IO.writeLine("Hello " + name)
