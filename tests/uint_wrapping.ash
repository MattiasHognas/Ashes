// expect: 0|255|254
// Wrapping arithmetic: 255u8 + 1u8 = 0u8; ~0u8 = 255u8; ~1u8 = 254u8
let a = 255u8 + 1u8
in
    let b = ~0u8
    in
        let c = ~1u8
        in Ashes.IO.print(Ashes.Text.fromInt(a) + "|" + Ashes.Text.fromInt(b) + "|" + Ashes.Text.fromInt(c))
