// expect: nested-one-int passed
let recursive same left right = left == right

if same(1)(if same("text")("text")
then 1
else 2)
then Ashes.IO.print("nested-one-int passed")
else Ashes.IO.panic("nested-one-int produced a wrong result")
