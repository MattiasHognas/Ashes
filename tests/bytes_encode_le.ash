// expect: 2|1|4|3|2|1|8|7|6|5|4|3|2|1
let b16 = Ashes.Byte.u16Le(258u16)
in
    let b32 = Ashes.Byte.u32Le(16909060u32)
    in
        let b64 = Ashes.Byte.u64Le(72623859790382856u64)
        in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Byte.get(b16)(0)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b16)(1)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b32)(0)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b32)(1)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b32)(2)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b32)(3)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b64)(0)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b64)(1)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b64)(2)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b64)(3)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b64)(4)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b64)(5)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b64)(6)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b64)(7)))
