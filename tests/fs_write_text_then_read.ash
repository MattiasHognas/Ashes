// expect: hello
match Ashes.File.writeText("out.txt")("hello") with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(_) -> 
        match Ashes.File.readText("out.txt") with
            | Ok(text) -> Ashes.IO.print(text)
            | Error(msg) -> Ashes.IO.print(msg)
