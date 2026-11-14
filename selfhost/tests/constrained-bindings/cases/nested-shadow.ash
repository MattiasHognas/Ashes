// expect: nested-shadow passed
let recursive same left right = left == right

let check unit =
    (let same left right =
        if left == right
        then false
        else false
    in same(1)(1) == false)

if same(1)(1) && check(Unit)
then Ashes.IO.print("nested-shadow passed")
else Ashes.IO.panic("a nested binding was replaced by an unrelated source copy")
