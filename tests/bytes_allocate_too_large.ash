// exit: 1
// expect: Bytes.allocate: length must be between 0 and 1073741824
1073741825
|> Ashes.Byte.allocate
|> Ashes.Byte.length
|> Ashes.IO.print
