// expect: intrinsic query passed
let recursive same left right = left == right

if same(Ashes.Text.fromInt(7))("7") && same(Ashes.Text.byteLength("four"))(4)
then Ashes.IO.print("intrinsic query passed")
else Ashes.IO.panic("intrinsic query failed")
