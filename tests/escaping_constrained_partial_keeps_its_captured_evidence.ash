// expect: passed
let recursive contains key values =
    match values with
        | [] -> false
        | head :: rest -> head == key || contains(key)(rest)

let makePartial n = contains(n)

let checkRound n =
    (let partial = makePartial(n)
    in partial([0, n]))

let recursive rounds count =
    if count == 0
    then Ashes.IO.print("passed")
    else
        if checkRound(count)
        then rounds(count - 1)
        else Ashes.IO.panic("failed")

rounds(1000)
