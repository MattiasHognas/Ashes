// expect: plain-before passed
let same left right = left == right

if same(1)(1) && same("text")("text")
then Ashes.IO.print("plain-before passed")
else Ashes.IO.panic("plain-before produced a wrong result")
