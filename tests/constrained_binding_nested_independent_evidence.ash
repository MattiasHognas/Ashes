// expect: nested constrained evidence passed
let recursive same left right = left == right

let integerOuter =
    same(1)(if same("text")("text")
    then 1
    else 2)

let stringOuter =
    same("text")(if same(1)(1)
    then "text"
    else "other")

let negative =
    same(1)(if same("text")("other")
    then 1
    else 2)

if integerOuter && stringOuter && negative == false
then Ashes.IO.print("nested constrained evidence passed")
else Ashes.IO.panic("nested calls shared an instantiation or dictionary")
