// expect: 3|10|20|30
let b = Ashes.Byte.appendByte(Ashes.Byte.appendByte(Ashes.Byte.singleton(10u8))(20u8))(30u8)
in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Byte.length(b)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b)(0)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b)(1)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b)(2)))
