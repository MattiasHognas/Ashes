// expect: escaped passed
let recursive same left right = left == right

let make key = same(key)

let one = make(1)

let text = make("x")

if one(1) && text("x")
then Ashes.IO.print("escaped passed")
else Ashes.IO.panic("escaped produced a wrong result")
