// expect: recursive dictionary forwarding passed
// Computed self arguments must receive the current dictionaries before TCO analysis.
let recursive repeat value remaining =
    if remaining == 0
    then value == value
    else
        repeat(if remaining % 2 == 0
        then value
        else value)(remaining - 1)

if repeat(7)(200000) && repeat("text")(200000)
then Ashes.IO.print("recursive dictionary forwarding passed")
else Ashes.IO.panic("recursive evidence changed")
