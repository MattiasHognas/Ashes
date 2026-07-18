// expect: 0
let b = Ashes.Byte.empty(Unit)
in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Byte.length(b)))
