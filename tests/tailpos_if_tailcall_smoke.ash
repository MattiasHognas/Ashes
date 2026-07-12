// expect: ok
let recursive f x =
    if x >= 1
    then 0
    else f(x + 1)
in Ashes.IO.print("ok")
