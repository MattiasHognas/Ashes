// n-body -- Benchmarks Game challenge.
//
// Simulate the Jovian planets (Sun, Jupiter, Saturn, Uranus, Neptune) under Newtonian gravity for
// N symplectic-integrator timesteps and print the total system energy before and after, to 9
// decimal places. Each body is a record of position/velocity/mass; the system is a small fixed
// list. The advance step folds pairwise gravitational interactions (each needing a square root)
// into new velocities, then updates positions. Energy is kinetic minus pairwise potential.
//
// Usage: ./n-body 1000   (defaults to 1000)
import Ashes.IO as io
import Ashes.Math as math
import Ashes.Text as text
type Body =
    | x: Float
    | y: Float
    | z: Float
    | vx: Float
    | vy: Float
    | vz: Float
    | mass: Float

let pi = 3.141592653589793

let solarMass = 4.0 * pi * pi

let daysPerYear = 365.24

let dt = 0.01

let sun = Body(x = 0.0, y = 0.0, z = 0.0, vx = 0.0, vy = 0.0, vz = 0.0, mass = solarMass)

let jupiter = Body(x = 4.84143144246472090, y = -1.16032004402742839, z = -0.103622044471123109, vx = 0.00166007664274403694 * daysPerYear, vy = 0.00769901118419740425 * daysPerYear, vz = -0.0000690460016972063023 * daysPerYear, mass = 0.000954791938424326609 * solarMass)

let saturn = Body(x = 8.34336671824457987, y = 4.12479856412430479, z = -0.403523417114321381, vx = -0.00276742510726862411 * daysPerYear, vy = 0.00499852801234917238 * daysPerYear, vz = 0.0000230417297573763929 * daysPerYear, mass = 0.000285885980666130812 * solarMass)

let uranus = Body(x = 12.8943695621391310, y = -15.1111514016986312, z = -0.223307578892655734, vx = 0.00296460137564761618 * daysPerYear, vy = 0.00237847173959480950 * daysPerYear, vz = -0.0000296589568540237556 * daysPerYear, mass = 0.0000436624404335156298 * solarMass)

let neptune = Body(x = 15.3796971148509165, y = -25.9193146099879641, z = 0.179258772950371181, vx = 0.00268067772490389322 * daysPerYear, vy = 0.00162824170038242295 * daysPerYear, vz = -0.0000951592254519715870 * daysPerYear, mass = 0.0000515138902046611451 * solarMass)

let recursive sumMomentum bodies px py pz =
    match bodies with
        | [] -> (px, py, pz)
        | b :: rest ->
            match b with
                | Body(x, y, z, vx, vy, vz, mass) -> sumMomentum(rest)(vx * mass + px)(vy * mass + py)(vz * mass + pz)

let offsetSun system =
    match sumMomentum(system)(0.0)(0.0)(0.0) with
        | (px, py, pz) ->
            match system with
                | s :: rest ->
                    match s with
                        | Body(x, y, z, vx, vy, vz, mass) -> Body(x = x, y = y, z = z, vx = (0.0 - px) / solarMass, vy = (0.0 - py) / solarMass, vz = (0.0 - pz) / solarMass, mass = mass) :: rest
                | [] -> system

let recursive accel dt bx by bz i j others avx avy avz =
    match others with
        | [] -> (avx, avy, avz)
        | o :: rest ->
            if i == j
            then accel(dt)(bx)(by)(bz)(i)(j + 1)(rest)(avx)(avy)(avz)
            else
                match o with
                    | Body(ox, oy, oz, ovx, ovy, ovz, omass) ->
                        let dx = bx - ox
                        in
                            let dy = by - oy
                            in
                                let dz = bz - oz
                                in
                                    let d2 = dx * dx + dy * dy + dz * dz
                                    in
                                        let mag = dt / (d2 * math.sqrt(d2))
                                        in accel(dt)(bx)(by)(bz)(i)(j + 1)(rest)(avx - dx * omass * mag)(avy - dy * omass * mag)(avz - dz * omass * mag)

let recursive updateVel dt allBodies i remaining =
    match remaining with
        | [] -> []
        | b :: rest ->
            match b with
                | Body(x, y, z, vx, vy, vz, mass) ->
                    match accel(dt)(x)(y)(z)(i)(0)(allBodies)(0.0)(0.0)(0.0) with
                        | (dvx, dvy, dvz) -> Body(x = x, y = y, z = z, vx = vx + dvx, vy = vy + dvy, vz = vz + dvz, mass = mass) :: updateVel(dt)(allBodies)(i + 1)(rest)

let recursive updatePos dt bodies =
    match bodies with
        | [] -> []
        | b :: rest ->
            match b with
                | Body(x, y, z, vx, vy, vz, mass) -> Body(x = x + dt * vx, y = y + dt * vy, z = z + dt * vz, vx = vx, vy = vy, vz = vz, mass = mass) :: updatePos(dt)(rest)

let advance dt bodies = updatePos(dt)(updateVel(dt)(bodies)(0)(bodies))

let recursive potential bx by bz bmass rest acc =
    match rest with
        | [] -> acc
        | o :: more ->
            match o with
                | Body(ox, oy, oz, ovx, ovy, ovz, omass) ->
                    let dx = bx - ox
                    in
                        let dy = by - oy
                        in
                            let dz = bz - oz
                            in
                                let dist = math.sqrt(dx * dx + dy * dy + dz * dz)
                                in potential(bx)(by)(bz)(bmass)(more)(acc - bmass * omass / dist)

let recursive energy bodies acc =
    match bodies with
        | [] -> acc
        | b :: rest ->
            match b with
                | Body(x, y, z, vx, vy, vz, mass) ->
                    let kinetic = 0.5 * mass * (vx * vx + vy * vy + vz * vz)
                    in energy(rest)(potential(x)(y)(z)(mass)(rest)(kinetic + acc))

let recursive run n dt bodies =
    if n == 0
    then bodies
    else run(n - 1)(dt)(advance(dt)(bodies))

let system = offsetSun([sun, jupiter, saturn, uranus, neptune])

let simulate n =
    (let final = run(n)(dt)(system)
    in
        let _ = io.print(text.formatFloat(energy(system)(0.0))(9))
        in io.print(text.formatFloat(energy(final)(0.0))(9)))

match io.args with
    | arg :: _ ->
        match text.parseInt(arg) with
            | Ok(n) -> simulate(n)
            | Error(_) -> simulate(1000)
    | [] -> simulate(1000)
