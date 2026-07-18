// file-bytes: bad.bin = FF FE FD
// expect: Ashes.IO.File.readText() encountered invalid UTF-8
match Ashes.IO.File.readText("bad.bin") with
    | Ok(text) -> Ashes.IO.print(text)
    | Error(msg) -> Ashes.IO.print(msg)
