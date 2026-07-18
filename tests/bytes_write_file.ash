// expect: hello
// Write bytes that represent "hello" (ASCII) then read back as text
let b = Ashes.Byte.appendByte(Ashes.Byte.appendByte(Ashes.Byte.appendByte(Ashes.Byte.appendByte(Ashes.Byte.singleton(104u8))(101u8))(108u8))(108u8))(111u8)
in
    match Ashes.IO.File.writeBytes("bytes_out.txt")(b) with
        | Error(msg) -> Ashes.IO.print(msg)
        | Ok(_) ->
            match Ashes.IO.File.readText("bytes_out.txt") with
                | Ok(text) -> Ashes.IO.print(text)
                | Error(msg) -> Ashes.IO.print(msg)
