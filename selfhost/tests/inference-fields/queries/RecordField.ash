// expect: record field query passed
type Box =
    | value: Int

let recursive same left right = left == right

let box = Box(value = 7)

if same(box.value)(7)
then Ashes.IO.print("record field query passed")
else Ashes.IO.panic("record field query failed")
