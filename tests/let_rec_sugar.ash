// expect: 10
let rec loop i = 
    if i >= 10
    then i
    else loop(i + 1)
in 
    0
    |> loop
    |> Ashes.IO.print
