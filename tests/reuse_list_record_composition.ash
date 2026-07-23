// A fresh list-of-records result may be consumed by an immediate recursive rewriter. The
// rewriter mutates only that still-local arena graph; the enclosing escape boundary performs the
// ordinary RC normalization afterward.
// expect: 2.0 2.0 2.0
import Ashes.IO as io
import Ashes.Text as text
type Body =
    | x: Float
    | velocity: Float

let recursive makeBodies count =
    if count == 0
    then []
    else Body(x = 0.0, velocity = 2.0) :: makeBodies(count - 1)

let recursive moveBodies dt bodies =
    match bodies with
        | [] -> []
        | body :: rest ->
            match body with
                | Body(x, velocity) -> Body(x = x + dt * velocity, velocity = velocity) :: moveBodies(dt)(rest)

let advance dt _ = moveBodies(dt)(makeBodies(3))

let recursive run turns bodies =
    if turns == 0
    then bodies
    else run(turns - 1)(advance(1.0)(bodies))

let recursive positions bodies output =
    match bodies with
        | [] -> output
        | body :: rest ->
            match body with
                | Body(x, _) ->
                    let separator =
                        if output == ""
                        then ""
                        else " "
                    in positions(rest)(output + separator + text.formatFloat(x)(1))

io.print(positions(run(10)([]))(""))
