let first = Ashes.IO.readLine(Unit)
in 
    let second = Ashes.IO.readLine(Unit)
    in 
        match first with
            | None -> Ashes.IO.writeLine("no first line")
            | Some(a) -> 
                match second with
                    | None -> Ashes.IO.writeLine(a)
                    | Some(b) -> Ashes.IO.writeLine(a + ":" + b)
