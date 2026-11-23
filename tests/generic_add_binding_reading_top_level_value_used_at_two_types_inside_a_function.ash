// expect: 12 4.5 3
let times = 3

let recursive repeatAdd total x n =
    if n <= 0
    then total
    else repeatAdd(total + x)(x)(n - 1)

let triple x = repeatAdd(x)(x)(times - 1)

let describe n =
    Ashes.Text.fromInt(triple(n)) + " " + Ashes.Text.fromFloat(triple(1.5)) + " " + Ashes.Text.fromInt(repeatAdd(0)(1)(times))

4
|> describe
|> Ashes.IO.print
