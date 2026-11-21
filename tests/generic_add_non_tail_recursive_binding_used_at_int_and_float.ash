// expect: 8 4.5 8 6.0
let times = 3

let recursive sumWith offset n =
    if n <= 0
    then offset
    else offset + sumWith(offset)(n - 1)

let recursive addTimes x n =
    if n >= times
    then x
    else x + addTimes(x)(n + 1)

Ashes.IO.print(Ashes.Text.fromInt(sumWith(2)(3)) + " " + Ashes.Text.fromFloat(sumWith(1.5)(2)) + " " + Ashes.Text.fromInt(addTimes(2)(0)) + " " + Ashes.Text.fromFloat(addTimes(1.5)(0)))
