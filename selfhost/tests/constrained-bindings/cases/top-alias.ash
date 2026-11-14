// expect: top-alias passed
let recursive same left right = left == right

let alias = same

if alias(1)(1) && alias("x")("x")
then Ashes.IO.print("top-alias passed")
else Ashes.IO.panic("top-alias produced a wrong result")
