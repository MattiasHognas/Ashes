import Input as input
import Ashes.Math as math

let courtWidth = 62

let courtHeight = 18

let playerColumn = 1

let cpuColumn = 60

let playerPlane = 2.0

let cpuPlane = 59.0

let paddleHalf = 1.5

let paddleReach = 2.1

let winningScore = 5

let tickSeconds = 0.033

let serveSpeed = 15.0

let maxBallSpeed = 34.0

let cpuMaxSpeed = 8.5

let mouseRowOffset = 3

let centerX = 31.0

let centerY = 8.5

type State =
    | ballX: Float
    | ballY: Float
    | velX: Float
    | velY: Float
    | playerY: Float
    | cpuY: Float
    | playerScore: Int
    | cpuScore: Int
    | pointsPlayed: Int

let initialState = State(ballX = 31.0, ballY = 8.5, velX = 0.0 - 15.0, velY = 2.5, playerY = 8.5, cpuY = 8.5, playerScore = 0, cpuScore = 0, pointsPlayed = 1)

let clampPaddle y = math.clampF(paddleHalf)(math.toFloat(courtHeight - 1) - paddleHalf)(y)

let clampVy vy = math.clampF(0.0 - 18.0)(18.0)(vy)

let frac x = x - math.floor(x)

let noise seed = frac(math.absF(math.sin(seed * 12.9898) * 43758.5453))

let serveVelY points = (noise(math.toFloat(points) * 7.31) - 0.5) * 12.0

let serveBall (state: State) direction = state with ballX = centerX, ballY = centerY, velX = math.toFloat(direction) * serveSpeed, velY = serveVelY(state.pointsPlayed), pointsPlayed = state.pointsPlayed + 1

let applyEvent (state: State) event =
    match event with
        | Up -> state with playerY = clampPaddle(state.playerY - 1.0)
        | Down -> state with playerY = clampPaddle(state.playerY + 1.0)
        | MouseRow(screenRow) -> state with playerY = clampPaddle(math.toFloat(screenRow - mouseRowOffset))
        | Quit -> state

let recursive applyEvents (state: State) events =
    match events with
        | [] -> state
        | event :: rest -> applyEvents(applyEvent(state)(event))(rest)

let moveCpu (state: State) =
    (let target =
        if state.velX > 0.0
        then state.ballY
        else centerY
    in
        let delta = target - state.cpuY
        in
            if math.absF(delta) < 0.3
            then state
            else
                let stepAmount = math.clampF(0.0 - cpuMaxSpeed * tickSeconds)(cpuMaxSpeed * tickSeconds)(delta)
                in state with cpuY = clampPaddle(state.cpuY + stepAmount))

let bounceY y vy =
    if y < 0.0
    then (0.0 - y, math.absF(vy))
    else
        let maxY = math.toFloat(courtHeight - 1)
        in
            if y > maxY
            then (maxY - (y - maxY), 0.0 - math.absF(vy))
            else (y, vy)

let crossedPlayer (state: State) nx =
    if state.velX < 0.0
    then
        if nx <= playerPlane
        then state.ballX > playerPlane
        else false
    else false

let crossedCpu (state: State) nx =
    if state.velX > 0.0
    then
        if nx >= cpuPlane
        then state.ballX < cpuPlane
        else false
    else false

let rallySpeed vx = math.minF(maxBallSpeed)(math.absF(vx) * 1.06)

let advanceBall (state: State) =
    (let nx = state.ballX + state.velX * tickSeconds
    in
        match bounceY(state.ballY + state.velY * tickSeconds)(state.velY) with
            | (ny, vy) ->
                if crossedPlayer(state)(nx)
                then
                    if math.absF(ny - state.playerY) <= paddleReach
                    then state with ballX = playerPlane + playerPlane - nx, ballY = ny, velX = rallySpeed(state.velX), velY = clampVy(vy + (ny - state.playerY) * 3.5)
                    else state with ballX = nx, ballY = ny, velY = vy
                else
                    if crossedCpu(state)(nx)
                    then
                        if math.absF(ny - state.cpuY) <= paddleReach
                        then state with ballX = cpuPlane - (nx - cpuPlane), ballY = ny, velX = 0.0 - rallySpeed(state.velX), velY = clampVy(vy + (ny - state.cpuY) * 3.5)
                        else state with ballX = nx, ballY = ny, velY = vy
                    else
                        if nx < 0.0 - 1.0
                        then serveBall(state with cpuScore = state.cpuScore + 1)(-1)
                        else
                            if nx > math.toFloat(courtWidth) + 1.0
                            then serveBall(state with playerScore = state.playerScore + 1)(1)
                            else state with ballX = nx, ballY = ny, velY = vy)

let step state events = advanceBall(moveCpu(applyEvents(state)(events)))

let finished (state: State) =
    if state.playerScore >= winningScore
    then true
    else state.cpuScore >= winningScore
