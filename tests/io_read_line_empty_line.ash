// stdin: \n
// expect: empty
match Ashes.IO.readLine(Unit) with
    | None -> Ashes.IO.print("none")
    | Some(text) -> 
        if text == ""
        then Ashes.IO.print("empty")
        else Ashes.IO.print(text)
