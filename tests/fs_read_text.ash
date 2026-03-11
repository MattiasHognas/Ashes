// file: hello.txt = hello
// expect: hello
match Ashes.Fs.readText("hello.txt") with
    | Ok(text) -> Ashes.IO.print(text)
    | Error(msg) -> Ashes.IO.print(msg)
