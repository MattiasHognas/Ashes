// expect: partial passed
let recursive same left right = left == right

let one = same(1)

let text = same("x")

if one(1) && text("x")
then Ashes.IO.print("partial passed")
else Ashes.IO.panic("partial produced a wrong result")
