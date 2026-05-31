// expect: 4|7|3
// Bitwise AND, OR, XOR on u8 values
let a = 12u8 & 6u8
in let b = 5u8 | 3u8
in let c = 5u8 ^ 6u8
in Ashes.IO.print(Ashes.Text.fromInt(a) + "|" + Ashes.Text.fromInt(b) + "|" + Ashes.Text.fromInt(c))
