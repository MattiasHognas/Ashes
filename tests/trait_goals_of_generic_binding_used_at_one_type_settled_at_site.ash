// expect: 41 yes
let recursive isMember target items =
    match items with
        | [] -> false
        | first :: rest ->
            if first == target
            then true
            else isMember(target)(rest)

let bump x = x + Ashes.Trait.Default.default(Unit)

let doubledListed x = isMember(x + x)([x, x + x])

let answer flag =
    if flag
    then "yes"
    else "no"

Ashes.IO.print(Ashes.Text.fromInt(bump(41)) + " " + answer(doubledListed(3)))
