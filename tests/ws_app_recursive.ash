// expect: 100000
let rec loop x y = 
    if x >= 100000
    then y
    else loop(x + 1)(y + 1)
in Ashes.IO.print(loop 0 0)
