// expect: builtin-argument passed
let recursive same left right = left == right

if same(Ashes.Text.fromInt(1))("1")
then Ashes.IO.print("builtin-argument passed")
else Ashes.IO.panic("builtin-argument produced a wrong result")
