// expect: 100000
let rec loop i acc = 
    if i >= 100000
    then acc
    else loop(i + 1)(acc + 1)
in Ashes.IO.print(loop(0)(0))
