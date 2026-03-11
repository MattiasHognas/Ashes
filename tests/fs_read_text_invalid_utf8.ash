// file-bytes: bad.bin = FF FE FD
// expect: Ashes.File.readText() encountered invalid UTF-8
match Ashes.File.readText("bad.bin") with
    | Ok(text) -> Ashes.IO.print(text)
    | Error(msg) -> Ashes.IO.print(msg)
