// expect: nested-alias passed
let recursive same left right = left == right

let check unit =
    (let alias = same
    in alias(1)(1) && alias("x")("x"))

if check(Unit)
then Ashes.IO.print("nested-alias passed")
else Ashes.IO.panic("nested-alias produced a wrong result")
