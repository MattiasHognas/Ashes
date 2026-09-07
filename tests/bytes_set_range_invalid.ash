// exit: 1
// expect: Bytes.setU64Le: range out of bounds
1u64
|> Ashes.Byte.setU64Le(Ashes.Byte.allocate(8))(1)
|> Ashes.Byte.length
|> Ashes.IO.print
