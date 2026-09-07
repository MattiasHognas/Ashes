// expect: 100000
let recursive loop i acc =
    if i >= 100000
    then acc
    else loop(i + 1)(acc + 1)
in
    0
    |> loop(0)
    |> Ashes.IO.print
