// exit: 1
// expect: Bytes.setU64Le: range out of bounds
Ashes.IO.print(Ashes.Byte.length(Ashes.Byte.setU64Le(Ashes.Byte.allocate(8))(1)(1u64)))
