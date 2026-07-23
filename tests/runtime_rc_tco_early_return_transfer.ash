// Regression: an early return lowered before a later self-call promotes a TCO parameter still
// transfers that parameter's RC ownership to the function result.
// expect: 84
import Ashes.IO as io
type Box =
    | value: Int

let rebuild values =
    match values with
        | [] -> values
        | Box(value) :: rest -> Box(value = value) :: rest

let recursive passThrough n values =
    if n == 0
    then values
    else passThrough(n - 1)(rebuild(values))

let recursive sum values total =
    match values with
        | [] -> total
        | Box(value) :: rest -> sum(rest)(total + value)

let source = rebuild([Box(value = 40), Box(value = 2)])

let returned = passThrough(0)(source)

io.print(sum(source)(0) + sum(returned)(0))
