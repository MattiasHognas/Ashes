import Ansi as ansi
import Physics as physics
import Physics.State
import Ashes.Math as math
import Ashes.Text as text

let netColumn = 31

let paddleCell col row playerRow cpuRow =
    if col == physics.playerColumn
    then
        if math.abs(row - playerRow) <= 1
        then ansi.green("#")
        else " "
    else
        if col == physics.cpuColumn
        then
            if math.abs(row - cpuRow) <= 1
            then ansi.red("#")
            else " "
        else
            if col == netColumn
            then
                if row / 2 * 2 == row
                then ansi.dim("|")
                else " "
            else " "

let cellAt col row ballCol ballRow playerRow cpuRow =
    if col == ballCol
    then
        if row == ballRow
        then ansi.yellow("o")
        else paddleCell(col)(row)(playerRow)(cpuRow)
    else paddleCell(col)(row)(playerRow)(cpuRow)

let recursive renderRow col row ballCol ballRow playerRow cpuRow acc =
    if col >= physics.courtWidth
    then acc
    else renderRow(col + 1)(row)(ballCol)(ballRow)(playerRow)(cpuRow)(acc + cellAt(col)(row)(ballCol)(ballRow)(playerRow)(cpuRow))

let recursive renderRows row ballCol ballRow playerRow cpuRow acc =
    if row >= physics.courtHeight
    then acc
    else renderRows(row + 1)(ballCol)(ballRow)(playerRow)(cpuRow)(acc + renderRow(0)(row)(ballCol)(ballRow)(playerRow)(cpuRow)("") + "\n")

let recursive tableEdge col acc =
    if col >= physics.courtWidth
    then acc
    else tableEdge(col + 1)(acc + "=")

let scoreLine (state: State) = ansi.green("you " + text.fromInt(state.playerScore)) + "  " + ansi.red("cpu " + text.fromInt(state.cpuScore)) + "  " + ansi.dim("first to " + text.fromInt(physics.winningScore) + " | w/s, arrows or mouse | q quits")

let renderFrame (state: State) = ansi.home + scoreLine(state) + "\n" + ansi.blue(tableEdge(0)("")) + "\n" + renderRows(0)(math.roundToInt(state.ballX))(math.roundToInt(state.ballY))(math.roundToInt(state.playerY))(math.roundToInt(state.cpuY))("") + ansi.blue(tableEdge(0)(""))

let finalLine (state: State) =
    if state.playerScore >= physics.winningScore
    then ansi.green("you win " + text.fromInt(state.playerScore) + "-" + text.fromInt(state.cpuScore))
    else
        if state.cpuScore >= physics.winningScore
        then ansi.red("the cpu wins " + text.fromInt(state.cpuScore) + "-" + text.fromInt(state.playerScore))
        else ansi.dim("rally stopped at " + text.fromInt(state.playerScore) + "-" + text.fromInt(state.cpuScore))
