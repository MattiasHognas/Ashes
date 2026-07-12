// expect: hello
// Write bytes that represent "hello" (ASCII) then read back as text
let b = Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.singleton(104u8))(101u8))(108u8))(108u8))(111u8)
in
    match Ashes.File.writeBytes("bytes_out.txt")(b) with
        | Error(msg) -> Ashes.IO.print(msg)
        | Ok(_) ->
            match Ashes.File.readText("bytes_out.txt") with
                | Ok(text) -> Ashes.IO.print(text)
                | Error(msg) -> Ashes.IO.print(msg)
