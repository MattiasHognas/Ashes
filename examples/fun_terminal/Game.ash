import Ansi as ansi
import Physics as physics
import Ashes.Math as math
import Ashes.Regex as regex
import Ashes.String as str
import Ashes.Text as text
let playerX = 3

let computerX = 58

let boardHeight = 13

let shotPattern = regex.compile("^\\s*(\\d{1,2})\\s+(\\d{1,3})\\s*$")

let parseShot line =
    match shotPattern with
        | Error(bad) -> Error("bad pattern: " + bad)
        | Ok(pattern) ->
            match regex.captures(pattern)(line) with
                | None -> Error("type angle and power, like: 45 70")
                | Some(groups) ->
                    match groups with
                        | _full :: Some(angleText) :: Some(powerText) :: [] ->
                            match text.parseInt(angleText) with
                                | Error(_badAngle) -> Error("angle must be a number")
                                | Ok(angle) ->
                                    match text.parseInt(powerText) with
                                        | Error(_badPower) -> Error("power must be a number")
                                        | Ok(power) ->
                                            if angle < 10
                                            then Error("angle must be between 10 and 80")
                                            else
                                                if angle > 80
                                                then Error("angle must be between 10 and 80")
                                                else
                                                    if power < 10
                                                    then Error("power must be between 10 and 99")
                                                    else
                                                        if power > 99
                                                        then Error("power must be between 10 and 99")
                                                        else Ok((angle, power))
                        | _other -> Error("type angle and power, like: 45 70")

let recursive onTrail col row trail =
    match trail with
        | [] -> false
        | (x, y) :: rest ->
            if x == col
            then
                if y == row
                then true
                else onTrail(col)(row)(rest)
            else onTrail(col)(row)(rest)

let cellAt col row trail =
    if onTrail(col)(row)(trail)
    then ansi.yellow("o")
    else
        if col == physics.netX
        then
            if row < physics.netHeight
            then ansi.cyan("|")
            else " "
        else
            if col == playerX
            then
                if row < 3
                then ansi.green("#")
                else " "
            else
                if col == computerX
                then
                    if row < 3
                    then ansi.red("#")
                    else " "
                else " "

let recursive renderRow col row trail acc =
    if col >= physics.tableWidth
    then acc
    else renderRow(col + 1)(row)(trail)(acc + cellAt(col)(row)(trail))

let recursive renderRows row trail acc =
    if row < 0
    then acc
    else renderRows(row - 1)(trail)(acc + renderRow(0)(row)(trail)("") + "\n")

let recursive tableEdge col acc =
    if col >= physics.tableWidth
    then acc
    else tableEdge(col + 1)(acc + "=")

let renderBoard trail = renderRows(boardHeight)(trail)("") + ansi.blue(tableEdge(0)("")) + "\n"

let windLabel wind =
    if wind > 0.6
    then "wind --> " + text.fromFloat(wind)
    else
        if wind < 0.0 - 0.6
        then "wind <-- " + text.fromFloat(math.absF(wind))
        else "wind ~ calm"

let scoreLine playerScore computerScore wind = ansi.green("you " + text.fromInt(playerScore)) + "  " + ansi.red("cpu " + text.fromInt(computerScore)) + "  " + ansi.dim(windLabel(wind))

let landedNear target land = math.abs(land - target) <= 3

let aiJitter round =
    (let magnitude = math.max(0)(10 - round * 2)
    in
        let noise = physics.frac(math.absF(math.sin(math.toFloat(round) * 78.233) * 12543.21))
        in math.roundToInt((noise - 0.5) * 2.0 * math.toFloat(magnitude)))

let aiShot round =
    (let angle = 55
    in
        let power = physics.idealPower(computerX)(playerX + aiJitter(round))(angle)
        in (angle, power))
