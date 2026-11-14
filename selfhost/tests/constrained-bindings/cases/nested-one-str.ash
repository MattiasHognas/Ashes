// expect: nested-one-str passed
let recursive same left right = left == right

if same("text")(if same(1)(1)
then "text"
else "other")
then Ashes.IO.print("nested-one-str passed")
else Ashes.IO.panic("nested-one-str produced a wrong result")
