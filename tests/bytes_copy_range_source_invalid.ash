// exit: 1
// expect: Bytes.copyRange: source range out of bounds
Ashes.IO.print(Ashes.Byte.length(Ashes.Byte.copyRange(Ashes.Byte.allocate(4))(0)(Ashes.Byte.allocate(4))(-1)(1)))
