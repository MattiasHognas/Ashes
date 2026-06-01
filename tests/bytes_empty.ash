// expect: 0
let b = Ashes.Bytes.empty(Unit)
in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Bytes.length(b)))
