// expect: 1|65
let b = Ashes.Bytes.singleton(65u8)
in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Bytes.length(b)) + "|" + Ashes.Text.fromInt(Ashes.Bytes.get(b)(0)))
