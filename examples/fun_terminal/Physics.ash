import Ashes.Math as math
let tableWidth = 62

let netX = 31

let netHeight = 5

let gravity = 9.8

let degToRad degrees = math.toFloat(degrees) * math.pi / 180.0

let launchSpeed power = math.toFloat(power) * 0.32

let frac x = x - math.floor(x)

let windFor round =
    (let noise = frac(math.absF(math.sin(math.toFloat(round) * 12.9898) * 43758.5453))
    in (noise - 0.5) * 4.0)

type Flight =
    | NetBall(List((Int, Int)))
    | Landed(List((Int, Int)), Int)

let recursive fly x y vx vy wind trail steps =
    if steps <= 0
    then Landed(trail)(math.roundToInt(x))
    else
        let dt = 0.04
        in
            let nx = x + vx * dt
            in
                let ny = y + vy * dt
                in
                    let crossedNet =
                        if x <= math.toFloat(netX)
                        then nx > math.toFloat(netX)
                        else nx < math.toFloat(netX)
                    in
                        let underNetTop =
                            if crossedNet
                            then ny < math.toFloat(netHeight)
                            else false
                        in
                            if underNetTop
                            then NetBall(trail)
                            else
                                if ny <= 0.0
                                then Landed(trail)(math.roundToInt(nx))
                                else
                                    let point = (math.roundToInt(nx), math.roundToInt(ny))
                                    in fly(nx)(ny)(vx + wind * dt)(vy - gravity * dt)(wind)(point :: trail)(steps - 1)

let shoot fromX direction angle power wind =
    (let radians = degToRad(angle)
    in
        let speed = launchSpeed(power)
        in
            let vx = math.toFloat(direction) * speed * math.cos(radians)
            in
                let vy = speed * math.sin(radians)
                in fly(math.toFloat(fromX))(1.0)(vx)(vy)(wind)([])(600))

let idealPower fromX targetX angle =
    (let distance = math.absF(math.toFloat(targetX - fromX))
    in
        let radians = degToRad(angle)
        in
            let speed = math.sqrt(distance * gravity / math.sin(2.0 * radians))
            in math.clamp(10)(99)(math.roundToInt(speed / 0.32)))
