// exit: 1
// expect: Bytes.copyRange: destination range out of bounds
Ashes.IO.print(Ashes.Byte.length(Ashes.Byte.copyRange(Ashes.Byte.allocate(4))(3)(Ashes.Byte.allocate(4))(0)(2)))
