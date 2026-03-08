// stdin: hello\n
// expect: hello
match Ashes.IO.readLine(Unit) with
    | None -> Ashes.IO.print("none")
    | Some(text) -> Ashes.IO.print(text)
