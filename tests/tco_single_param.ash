// expect: 10
let recursive loop i =
    if i >= 10
    then i
    else loop(i + 1)
in Ashes.IO.print(loop(0))
