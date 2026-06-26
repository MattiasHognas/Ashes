// stdin: hello\n
// expect: ok
match Ashes.IO.readExact(5) with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(text) -> 
        if text == "hello"
        then Ashes.IO.print("ok")
        else Ashes.IO.print(text)
