// expect: 258|16909060|0x102030405060708
// Build a 14-byte buffer: [2,1, 4,3,2,1, 8,7,6,5,4,3,2,1] then decode LE values
let buf = Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.singleton(2u8))(1u8))(4u8))(3u8))(2u8))(1u8))(8u8))(7u8))(6u8))(5u8))(4u8))(3u8))(2u8))(1u8)
in
    let v16 = Ashes.Bytes.getU16Le(buf)(0)
    in
        let v32 = Ashes.Bytes.getU32Le(buf)(2)
        in
            let v64 = Ashes.Bytes.getU64Le(buf)(6)
            in Ashes.IO.print(Ashes.Text.fromInt(v16) + "|" + Ashes.Text.fromInt(v32) + "|" + Ashes.Text.toHex(v64))
