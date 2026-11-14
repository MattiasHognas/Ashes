// expect: qualified trait query passed
let recursive same left right = left == right

if same(Ashes.Trait.Show.show(7))("7")
then Ashes.IO.print("qualified trait query passed")
else Ashes.IO.panic("qualified trait query failed")
