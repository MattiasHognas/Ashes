// expect: hello
match Ashes.IO.File.writeText("out.txt")("hello") with
    | Error(msg) -> Ashes.IO.print(msg)
    | Ok(_) ->
        match Ashes.IO.File.readText("out.txt") with
            | Ok(text) -> Ashes.IO.print(text)
            | Error(msg) -> Ashes.IO.print(msg)
