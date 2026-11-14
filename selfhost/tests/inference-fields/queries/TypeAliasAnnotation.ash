// expect: alias annotation query passed
type alias Name = Str

let recursive same left right = left == right

if same(((given (value: Name) -> value))("name"))("name")
then Ashes.IO.print("alias annotation query passed")
else Ashes.IO.panic("alias annotation query failed")
