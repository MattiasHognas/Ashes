// file-bytes: bad.bin = FF FE FD
// expect: Ashes.Fs.readText() encountered invalid UTF-8
match Ashes.Fs.readText("bad.bin") with
    | Ok(text) -> Ashes.IO.print(text)
    | Error(msg) -> Ashes.IO.print(msg)
