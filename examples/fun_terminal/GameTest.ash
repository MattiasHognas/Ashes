import Ansi as ansi
import Game as game
import Input as input
import Physics as physics
import Physics.State
import Ashes.IO as io
import Ashes.String as str
import Ashes.Test.assertEqual
import Ashes.Text as text
let checkText label (expected: Str) (actual: Str) =
    (let _ = assertEqual(expected)(actual)
    in io.writeLine("ok - " + label))

let checkInt label (expected: Int) (actual: Int) =
    actual
    |> text.fromInt
    |> checkText(label)(text.fromInt(expected))

let checkFloat label (expected: Float) (actual: Float) =
    2
    |> text.formatFloat(actual)
    |> checkText(label)(text.formatFloat(expected)(2))

let boolText flag =
    if flag
    then "yes"
    else "no"

let checkBool label (expected: Bool) (actual: Bool) =
    actual
    |> boolText
    |> checkText(label)(boolText(expected))

let eventText event =
    match event with
        | Up -> "up"
        | Down -> "down"
        | MouseRow(row) -> "mouse:" + text.fromInt(row)
        | Quit -> "quit"

let recursive eventsText events =
    match events with
        | [] -> ""
        | event :: rest -> eventText(event) + ";" + eventsText(rest)

let decodedText pending =
    match input.decode(pending) with
        | (events, leftover) -> eventsText(events) + "leftover=" + leftover

let decodesPlainKeys _ =
    "ws"
    |> decodedText
    |> checkText("w and s decode to up and down")("up;down;leftover=")

let decodesArrowKeys _ =
    ansi.esc + "[A" + ansi.esc + "[B"
    |> decodedText
    |> checkText("arrow keys decode to up and down")("up;down;leftover=")

let decodesQuit _ =
    "q"
    |> decodedText
    |> checkText("q decodes to quit")("quit;leftover=")

let decodesMouse _ =
    ansi.esc + "[<35;40;12M"
    |> decodedText
    |> checkText("sgr mouse motion decodes to a row")("mouse:12;leftover=")

let keepsPartialSequence _ =
    ansi.esc + "["
    |> decodedText
    |> checkText("a partial escape sequence is kept for the next frame")("leftover=" + ansi.esc + "[")

let skipsUnknownSequence _ =
    ansi.esc + "[5~w"
    |> decodedText
    |> checkText("an unknown csi sequence is skipped")("up;leftover=")

let movesPaddleUp _ =
    (let next = physics.step(physics.initialState)([Up])
    in checkFloat("up moves the paddle one row up")(7.5)(next.playerY))

let mouseSetsPaddleRow _ =
    (let next = physics.step(physics.initialState)([MouseRow(10)])
    in checkFloat("a mouse row maps through the board offset")(7.0)(next.playerY))

let ballBouncesOffTop _ =
    (let base = physics.initialState
    in
        let low = base with ballY = 0.2, velY = 0.0 - 12.0
        in
            let next = physics.step(low)([])
            in checkBool("the ball bounces off the top wall")(true)(next.velY > 0.0))

let paddleReturnsBall _ =
    (let base = physics.initialState
    in
        let incoming = base with ballX = 2.2, velX = 0.0 - 15.0
        in
            let next = physics.step(incoming)([])
            in checkBool("an aligned paddle returns the ball")(true)(next.velX > 0.0))

let missScoresForCpu _ =
    (let base = physics.initialState
    in
        let missed = base with ballX = 0.0 - 0.8, velX = 0.0 - 15.0, ballY = 2.0, playerY = 15.0
        in
            let next = physics.step(missed)([])
            in checkInt("a missed ball scores for the cpu and reserves")(1)(next.cpuScore))

let passingCpuScoresForYou _ =
    (let base = physics.initialState
    in
        let winner = base with ballX = 62.9, velX = 15.0, ballY = 2.0, cpuY = 15.0
        in
            let next = physics.step(winner)([])
            in checkInt("a ball past the cpu scores for you")(1)(next.playerScore))

let cpuTracksTheBall _ =
    (let base = physics.initialState
    in
        let approaching = base with velX = 15.0, ballY = 2.0
        in
            let next = physics.step(approaching)([])
            in checkBool("the cpu paddle tracks an approaching ball")(true)(next.cpuY < 8.5))

let fivePointsEndTheGame _ =
    (let base = physics.initialState
    in
        let ended = base with playerScore = 5
        in
            ended
            |> physics.finished
            |> checkBool("five points end the game")(true))

let boardShowsTheBall _ =
    "o"
    |> str.contains(game.renderFrame(physics.initialState))
    |> checkBool("the board draws the ball")(true)

let boardShowsThePaddles _ =
    "#"
    |> str.contains(game.renderFrame(physics.initialState))
    |> checkBool("the board draws the paddles")(true)

let allPassed _ = io.print("all tests passed")

Unit
|> decodesPlainKeys
|> decodesArrowKeys
|> decodesQuit
|> decodesMouse
|> keepsPartialSequence
|> skipsUnknownSequence
|> movesPaddleUp
|> mouseSetsPaddleRow
|> ballBouncesOffTop
|> paddleReturnsBall
|> missScoresForCpu
|> passingCpuScoresForYou
|> cpuTracksTheBall
|> fivePointsEndTheGame
|> boardShowsTheBall
|> boardShowsThePaddles
|> allPassed
