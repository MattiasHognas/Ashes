// expect: first
let helper left right = left == right

let compareWithFirst left right = helper(left)(right)

let check value =
    (let helper left right = left != right
    in compareWithFirst(value)(value) && !helper(value)(value))

if check(42)
then Ashes.IO.print("first")
else Ashes.IO.print("second")
