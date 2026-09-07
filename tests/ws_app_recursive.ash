// expect: 100000
let recursive loop x y =
    if x >= 100000
    then y
    else loop(x + 1)(y + 1)
in
    0
    |> loop 0
    |> Ashes.IO.print
