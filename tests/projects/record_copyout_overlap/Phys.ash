type Ball =
    | ballX: Float
    | ballY: Float
    | velX: Float
    | velY: Float

let start = Ball(ballX = 31.0, ballY = 8.5, velX = 0.0 - 15.0, velY = 2.5)

let identity (s: Ball) = s

let advance (state: Ball) =
    (let nx = state.ballX + state.velX * 0.033
    in
        match (state.ballY + state.velY * 0.033, state.velY) with
            | (ny, vy) ->
                if nx < 0.0 - 1.0
                then state with ballX = 31.0, ballY = 8.5
                else state with ballX = nx, ballY = ny, velY = vy)

let wrap state _ignored = advance(identity(state))
