import Ansi as ansi
import Game as game
import Physics as physics
import Ashes.IO as io
import Ashes.Text as text
let showShot who trail message playerScore computerScore wind =
    (let _cleared = io.write(ansi.clearScreen(Unit))
    in
        let _logo = io.writeLine(ansi.logo(Unit))
        in
            let _board = io.write(game.renderBoard(trail))
            in
                let _score = io.writeLine(game.scoreLine(playerScore)(computerScore)(wind))
                in io.writeLine(who + " " + message))

let resolveLanding shooterName targetX flight =
    match flight with
        | NetBall(trail) -> (trail, shooterName + " hit the net", false)
        | Landed(trail, land) ->
            if game.landedNear(targetX)(land)
            then (trail, shooterName + " smashed the paddle zone at " + text.fromInt(land) + " - point", true)
            else (trail, shooterName + " landed at " + text.fromInt(land) + " - no point", false)

let recursive gameLoop playerScore computerScore round =
    if playerScore >= 3
    then io.writeLine(ansi.green("you win the rally " + text.fromInt(playerScore) + "-" + text.fromInt(computerScore)))
    else
        if computerScore >= 3
        then io.writeLine(ansi.red("the computer wins " + text.fromInt(computerScore) + "-" + text.fromInt(playerScore)))
        else
            let wind = physics.windFor(round)
            in
                let _prompt = io.writeLine(game.scoreLine(playerScore)(computerScore)(wind))
                in
                    let _ask = io.write(ansi.cyan("your serve") + " angle power (10-80 10-99, q quits) > ")
                    in
                        match io.readLine(Unit) with
                            | None -> io.writeLine("bye")
                            | Some(line) ->
                                if line == "q"
                                then io.writeLine("bye")
                                else
                                    match game.parseShot(line) with
                                        | Error(problem) ->
                                            let _oops = io.writeLine(ansi.yellow(problem))
                                            in gameLoop(playerScore)(computerScore)(round)
                                        | Ok((angle, power)) ->
                                            match resolveLanding("you")(game.computerX)(physics.shoot(game.playerX)(1)(angle)(power)(wind)) with
                                                | (trail, message, scored) ->
                                                    let _shown = showShot(">")(trail)(message)(playerScore)(computerScore)(wind)
                                                    in
                                                        let newPlayerScore =
                                                            if scored
                                                            then playerScore + 1
                                                            else playerScore
                                                        in
                                                            match game.aiShot(round) with
                                                                | (aiAngle, aiPower) ->
                                                                    match resolveLanding("cpu")(game.playerX)(physics.shoot(game.computerX)(-1)(aiAngle)(aiPower)(wind)) with
                                                                        | (aiTrail, aiMessage, aiScored) ->
                                                                            let _aiShown = io.writeLine("< " + aiMessage + " (" + text.fromInt(aiAngle) + " " + text.fromInt(aiPower) + ")")
                                                                            in
                                                                                let newComputerScore =
                                                                                    if aiScored
                                                                                    then computerScore + 1
                                                                                    else computerScore
                                                                                in gameLoop(newPlayerScore)(newComputerScore)(round + 1)

let _welcome = io.write(ansi.clearScreen(Unit))

let _shownLogo = io.writeLine(ansi.logo(Unit))

gameLoop(0)(0)(1)
