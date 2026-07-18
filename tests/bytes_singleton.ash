// expect: 1|65
let b = Ashes.Byte.singleton(65u8)
in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Byte.length(b)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b)(0)))
