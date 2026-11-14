// expect: forward-one passed
let recursive same left right = left == right

let forward x y = same(x)(y)

if forward(1)(1) && forward("text")("text")
then Ashes.IO.print("forward-one passed")
else Ashes.IO.panic("forward-one produced a wrong result")
