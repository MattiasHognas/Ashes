// expect: nested-binder passed
let check unit =
    (let same left right = left == right
    in same(1)(1) && same("x")("x"))

if check(Unit)
then Ashes.IO.print("nested-binder passed")
else Ashes.IO.panic("nested-binder produced a wrong result")
