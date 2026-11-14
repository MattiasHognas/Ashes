// expect: external query passed
external strlen(Str) -> Int

let recursive same left right = left == right

if same(strlen("four"))(4)
then Ashes.IO.print("external query passed")
else Ashes.IO.panic("external query failed")
