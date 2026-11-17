// expect: recursive-member passed
let recursive member key entries =
    match entries with
        | [] -> false
        | head :: rest ->
            if key == head
            then true
            else member(key)(rest)

if member(1)([1]) && member("x")(["x"])
then Ashes.IO.print("recursive-member passed")
else Ashes.IO.panic("recursive-member produced a wrong result")
