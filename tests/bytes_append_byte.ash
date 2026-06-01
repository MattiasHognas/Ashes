// expect: 3|10|20|30
let b = Ashes.Bytes.appendByte(Ashes.Bytes.appendByte(Ashes.Bytes.singleton(10u8))(20u8))(30u8)
in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Bytes.length(b)) + "|" + Ashes.Text.fromInt(Ashes.Bytes.get(b)(0)) + "|" + Ashes.Text.fromInt(Ashes.Bytes.get(b)(1)) + "|" + Ashes.Text.fromInt(Ashes.Bytes.get(b)(2)))
