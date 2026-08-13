// exit: 1
// expect: Bytes.allocate: length must be between 0 and 1073741824
Ashes.IO.print(Ashes.Byte.length(Ashes.Byte.allocate(-1)))
