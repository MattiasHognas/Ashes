// exit: 1
// expect: Bytes.copyRange: source range out of bounds
1
|> Ashes.Byte.copyRange(Ashes.Byte.allocate(4))(0)(Ashes.Byte.allocate(4))(-1)
|> Ashes.Byte.length
|> Ashes.IO.print
