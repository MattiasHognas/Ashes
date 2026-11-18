// expect: 12 4.5
let times = 3

let recursive repeatAdd total x n =
    if n <= 0
    then total
    else repeatAdd(total + x)(x)(n - 1)

let triple x = repeatAdd(x)(x)(times - 1)

Ashes.IO.print(Ashes.Text.fromInt(triple(4)) + " " + Ashes.Text.fromFloat(triple(1.5)))
