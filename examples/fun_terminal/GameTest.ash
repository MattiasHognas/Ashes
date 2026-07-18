import Game as game
import Physics as physics
import Ashes.IO as io
import Ashes.Math as math
import Ashes.String as str
import Ashes.Test as test
import Ashes.Text as text
let check label expected actual =
    (let _ = test.assertEqual(expected)(actual)
    in io.writeLine("ok - " + label))

let shotText result =
    match result with
        | Error(problem) -> "error: " + problem
        | Ok((angle, power)) -> text.fromInt(angle) + "/" + text.fromInt(power)

let landText flight =
    match flight with
        | NetBall(_trail) -> "net"
        | Landed(_trail, land) -> "landed " + text.fromInt(land)

let landOf flight =
    match flight with
        | NetBall(_trail) -> -1
        | Landed(_trail, land) -> land

let parsesPlainShot _ =
    "45 70"
    |> game.parseShot
    |> shotText
    |> check("parses angle and power")("45/70")

let parsesPaddedShot _ =
    "  30   55  "
    |> game.parseShot
    |> shotText
    |> check("parses padded input")("30/55")

let rejectsWords _ =
    "fire the cannon"
    |> game.parseShot
    |> shotText
    |> check("rejects words")("error: type angle and power, like: 45 70")

let rejectsLowAngle _ =
    "5 50"
    |> game.parseShot
    |> shotText
    |> check("rejects a flat angle")("error: angle must be between 10 and 80")

let rejectsHotPower _ =
    "45 250"
    |> game.parseShot
    |> shotText
    |> check("rejects an overloaded cannon")("error: power must be between 10 and 99")

let clearsTheNet _ =
    0.0
    |> physics.shoot(game.playerX)(1)(45)(60)
    |> landOf
    |> (given (land) ->
        if land > physics.netX
        then "cleared"
        else "short")
    |> check("a 45 degree serve clears the net")("cleared")

let flatServeNets _ =
    0.0
    |> physics.shoot(game.playerX)(1)(10)(99)
    |> landText
    |> check("a flat hard serve hits the net")("net")

let windIsDeterministic _ =
    4
    |> physics.windFor
    |> text.fromFloat
    |> check("wind is deterministic per round")(4
    |> physics.windFor
    |> text.fromFloat)

let aiAimsNearPaddle _ =
    match game.aiShot(9) with
        | (angle, power) ->
            0.0
            |> physics.shoot(game.computerX)(-1)(angle)(power)
            |> landOf
            |> (given (land) ->
                if math.abs(land - game.playerX) <= 4
                then "close"
                else "wild: " + text.fromInt(land))
            |> check("late-round ai lands near the player paddle")("close")

let boardShowsTheNet _ =
    check("the board draws the net")("net drawn")(if str.contains(game.renderBoard([]))("|")
    then "net drawn"
    else "missing")

let allPassed _ = io.print("all tests passed")

Unit
|> parsesPlainShot
|> parsesPaddedShot
|> rejectsWords
|> rejectsLowAngle
|> rejectsHotPower
|> clearsTheNet
|> flatServeNets
|> windIsDeterministic
|> aiAimsNearPaddle
|> boardShowsTheNet
|> allPassed
