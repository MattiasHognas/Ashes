// exit: 1
// expect: Bytes.copyRange: destination range out of bounds
2
|> Ashes.Byte.copyRange(Ashes.Byte.allocate(4))(3)(Ashes.Byte.allocate(4))(0)
|> Ashes.Byte.length
|> Ashes.IO.print
