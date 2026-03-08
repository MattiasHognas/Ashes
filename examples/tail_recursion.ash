let rec loop i acc = 
    if i >= 10000
    then acc
    else loop(i + 1)(acc + 1)
in Ashes.IO.print(loop(0)(0))
