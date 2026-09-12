// n-body -- Benchmarks Game challenge.
//
// Simulate the Jovian planets (Sun, Jupiter, Saturn, Uranus, Neptune) under Newtonian gravity for
// N symplectic-integrator timesteps and print the total system energy before and after, to 9
// decimal places.
//
// The system is a fixed five-body `System` record rather than a `List(Body)`, and each pair is
// evaluated once: body i receives the interaction and body j receives its negation, so a step
// takes ten square roots instead of twenty. This is the shape the reference implementations use,
// and it is what the problem actually specifies -- five named planets, not an arbitrary N.
//
// Both choices matter for speed here. A list of five bodies rebuilds five cons cells and five
// records per step, and holding the whole list live to compute accelerations is genuine aliasing,
// so in-place reuse cannot fire on it; the fixed record has neither problem. The previous
// list-based formulation is kept beside this one as `n-body-list.ash`: it is the shape that
// exercises the arena/reference-counted list path, which is worth keeping runnable even though it
// is no longer the benchmark entry.
//
// Usage: ./n-body 1000   (defaults to 1000)
import Ashes.IO as io
import Ashes.Number.Math as math
import Ashes.Text as text
type Body =
    | x: Float
    | y: Float
    | z: Float
    | vx: Float
    | vy: Float
    | vz: Float
    | mass: Float

type System =
    | Sys(Body, Body, Body, Body, Body)

let pi = 3.141592653589793

let solarMass = 4.0 * pi * pi

let daysPerYear = 365.24

let dt = 0.01

let sun = Body(x = 0.0, y = 0.0, z = 0.0, vx = 0.0, vy = 0.0, vz = 0.0, mass = solarMass)

let jupiter = Body(x = 4.84143144246472090, y = -1.16032004402742839, z = -0.103622044471123109, vx = 0.00166007664274403694 * daysPerYear, vy = 0.00769901118419740425 * daysPerYear, vz = -0.0000690460016972063023 * daysPerYear, mass = 0.000954791938424326609 * solarMass)

let saturn = Body(x = 8.34336671824457987, y = 4.12479856412430479, z = -0.403523417114321381, vx = -0.00276742510726862411 * daysPerYear, vy = 0.00499852801234917238 * daysPerYear, vz = 0.0000230417297573763929 * daysPerYear, mass = 0.000285885980666130812 * solarMass)

let uranus = Body(x = 12.8943695621391310, y = -15.1111514016986312, z = -0.223307578892655734, vx = 0.00296460137564761618 * daysPerYear, vy = 0.00237847173959480950 * daysPerYear, vz = -0.0000296589568540237556 * daysPerYear, mass = 0.0000436624404335156298 * solarMass)

let neptune = Body(x = 15.3796971148509165, y = -25.9193146099879641, z = 0.179258772950371181, vx = 0.00268067772490389322 * daysPerYear, vy = 0.00162824170038242295 * daysPerYear, vz = -0.0000951592254519715870 * daysPerYear, mass = 0.0000515138902046611451 * solarMass)

let offsetSun s =
    match s with
        | Sys(b0, b1, b2, b3, b4) ->
            match b0 with
                | Body(x0, y0, z0, vx0, vy0, vz0, m0) ->
                    match b1 with
                        | Body(x1, y1, z1, vx1, vy1, vz1, m1) ->
                            match b2 with
                                | Body(x2, y2, z2, vx2, vy2, vz2, m2) ->
                                    match b3 with
                                        | Body(x3, y3, z3, vx3, vy3, vz3, m3) ->
                                            match b4 with
                                                | Body(x4, y4, z4, vx4, vy4, vz4, m4) ->
                                                    let px = vx0 * m0 + vx1 * m1 + vx2 * m2 + vx3 * m3 + vx4 * m4
                                                    in
                                                        let py = vy0 * m0 + vy1 * m1 + vy2 * m2 + vy3 * m3 + vy4 * m4
                                                        in
                                                            let pz = vz0 * m0 + vz1 * m1 + vz2 * m2 + vz3 * m3 + vz4 * m4
                                                            in Sys(Body(x = x0, y = y0, z = z0, vx = (0.0 - px) / solarMass, vy = (0.0 - py) / solarMass, vz = (0.0 - pz) / solarMass, mass = m0))(b1)(b2)(b3)(b4)

let advance s =
    match s with
        | Sys(b0, b1, b2, b3, b4) ->
            match b0 with
                | Body(x0, y0, z0, vx0, vy0, vz0, m0) ->
                    match b1 with
                        | Body(x1, y1, z1, vx1, vy1, vz1, m1) ->
                            match b2 with
                                | Body(x2, y2, z2, vx2, vy2, vz2, m2) ->
                                    match b3 with
                                        | Body(x3, y3, z3, vx3, vy3, vz3, m3) ->
                                            match b4 with
                                                | Body(x4, y4, z4, vx4, vy4, vz4, m4) ->
                                                    let dx01 = x0 - x1
                                                    in
                                                        let dy01 = y0 - y1
                                                        in
                                                            let dz01 = z0 - z1
                                                            in
                                                                let d201 = dx01 * dx01 + dy01 * dy01 + dz01 * dz01
                                                                in
                                                                    let mag01 = dt / (d201 * math.sqrt(d201))
                                                                    in
                                                                        let dx02 = x0 - x2
                                                                        in
                                                                            let dy02 = y0 - y2
                                                                            in
                                                                                let dz02 = z0 - z2
                                                                                in
                                                                                    let d202 = dx02 * dx02 + dy02 * dy02 + dz02 * dz02
                                                                                    in
                                                                                        let mag02 = dt / (d202 * math.sqrt(d202))
                                                                                        in
                                                                                            let dx03 = x0 - x3
                                                                                            in
                                                                                                let dy03 = y0 - y3
                                                                                                in
                                                                                                    let dz03 = z0 - z3
                                                                                                    in
                                                                                                        let d203 = dx03 * dx03 + dy03 * dy03 + dz03 * dz03
                                                                                                        in
                                                                                                            let mag03 = dt / (d203 * math.sqrt(d203))
                                                                                                            in
                                                                                                                let dx04 = x0 - x4
                                                                                                                in
                                                                                                                    let dy04 = y0 - y4
                                                                                                                    in
                                                                                                                        let dz04 = z0 - z4
                                                                                                                        in
                                                                                                                            let d204 = dx04 * dx04 + dy04 * dy04 + dz04 * dz04
                                                                                                                            in
                                                                                                                                let mag04 = dt / (d204 * math.sqrt(d204))
                                                                                                                                in
                                                                                                                                    let dx12 = x1 - x2
                                                                                                                                    in
                                                                                                                                        let dy12 = y1 - y2
                                                                                                                                        in
                                                                                                                                            let dz12 = z1 - z2
                                                                                                                                            in
                                                                                                                                                let d212 = dx12 * dx12 + dy12 * dy12 + dz12 * dz12
                                                                                                                                                in
                                                                                                                                                    let mag12 = dt / (d212 * math.sqrt(d212))
                                                                                                                                                    in
                                                                                                                                                        let dx13 = x1 - x3
                                                                                                                                                        in
                                                                                                                                                            let dy13 = y1 - y3
                                                                                                                                                            in
                                                                                                                                                                let dz13 = z1 - z3
                                                                                                                                                                in
                                                                                                                                                                    let d213 = dx13 * dx13 + dy13 * dy13 + dz13 * dz13
                                                                                                                                                                    in
                                                                                                                                                                        let mag13 = dt / (d213 * math.sqrt(d213))
                                                                                                                                                                        in
                                                                                                                                                                            let dx14 = x1 - x4
                                                                                                                                                                            in
                                                                                                                                                                                let dy14 = y1 - y4
                                                                                                                                                                                in
                                                                                                                                                                                    let dz14 = z1 - z4
                                                                                                                                                                                    in
                                                                                                                                                                                        let d214 = dx14 * dx14 + dy14 * dy14 + dz14 * dz14
                                                                                                                                                                                        in
                                                                                                                                                                                            let mag14 = dt / (d214 * math.sqrt(d214))
                                                                                                                                                                                            in
                                                                                                                                                                                                let dx23 = x2 - x3
                                                                                                                                                                                                in
                                                                                                                                                                                                    let dy23 = y2 - y3
                                                                                                                                                                                                    in
                                                                                                                                                                                                        let dz23 = z2 - z3
                                                                                                                                                                                                        in
                                                                                                                                                                                                            let d223 = dx23 * dx23 + dy23 * dy23 + dz23 * dz23
                                                                                                                                                                                                            in
                                                                                                                                                                                                                let mag23 = dt / (d223 * math.sqrt(d223))
                                                                                                                                                                                                                in
                                                                                                                                                                                                                    let dx24 = x2 - x4
                                                                                                                                                                                                                    in
                                                                                                                                                                                                                        let dy24 = y2 - y4
                                                                                                                                                                                                                        in
                                                                                                                                                                                                                            let dz24 = z2 - z4
                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                let d224 = dx24 * dx24 + dy24 * dy24 + dz24 * dz24
                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                    let mag24 = dt / (d224 * math.sqrt(d224))
                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                        let dx34 = x3 - x4
                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                            let dy34 = y3 - y4
                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                let dz34 = z3 - z4
                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                    let d234 = dx34 * dx34 + dy34 * dy34 + dz34 * dz34
                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                        let mag34 = dt / (d234 * math.sqrt(d234))
                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                            let nvx0 = vx0 - dx01 * m1 * mag01 - dx02 * m2 * mag02 - dx03 * m3 * mag03 - dx04 * m4 * mag04
                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                let nvx1 = vx1 + dx01 * m0 * mag01 - dx12 * m2 * mag12 - dx13 * m3 * mag13 - dx14 * m4 * mag14
                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                    let nvx2 = vx2 + dx02 * m0 * mag02 + dx12 * m1 * mag12 - dx23 * m3 * mag23 - dx24 * m4 * mag24
                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                        let nvx3 = vx3 + dx03 * m0 * mag03 + dx13 * m1 * mag13 + dx23 * m2 * mag23 - dx34 * m4 * mag34
                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                            let nvx4 = vx4 + dx04 * m0 * mag04 + dx14 * m1 * mag14 + dx24 * m2 * mag24 + dx34 * m3 * mag34
                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                let nvy0 = vy0 - dy01 * m1 * mag01 - dy02 * m2 * mag02 - dy03 * m3 * mag03 - dy04 * m4 * mag04
                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                    let nvy1 = vy1 + dy01 * m0 * mag01 - dy12 * m2 * mag12 - dy13 * m3 * mag13 - dy14 * m4 * mag14
                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                        let nvy2 = vy2 + dy02 * m0 * mag02 + dy12 * m1 * mag12 - dy23 * m3 * mag23 - dy24 * m4 * mag24
                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                            let nvy3 = vy3 + dy03 * m0 * mag03 + dy13 * m1 * mag13 + dy23 * m2 * mag23 - dy34 * m4 * mag34
                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                let nvy4 = vy4 + dy04 * m0 * mag04 + dy14 * m1 * mag14 + dy24 * m2 * mag24 + dy34 * m3 * mag34
                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                    let nvz0 = vz0 - dz01 * m1 * mag01 - dz02 * m2 * mag02 - dz03 * m3 * mag03 - dz04 * m4 * mag04
                                                                                                                                                                                                                                                                                                    in
                                                                                                                                                                                                                                                                                                        let nvz1 = vz1 + dz01 * m0 * mag01 - dz12 * m2 * mag12 - dz13 * m3 * mag13 - dz14 * m4 * mag14
                                                                                                                                                                                                                                                                                                        in
                                                                                                                                                                                                                                                                                                            let nvz2 = vz2 + dz02 * m0 * mag02 + dz12 * m1 * mag12 - dz23 * m3 * mag23 - dz24 * m4 * mag24
                                                                                                                                                                                                                                                                                                            in
                                                                                                                                                                                                                                                                                                                let nvz3 = vz3 + dz03 * m0 * mag03 + dz13 * m1 * mag13 + dz23 * m2 * mag23 - dz34 * m4 * mag34
                                                                                                                                                                                                                                                                                                                in
                                                                                                                                                                                                                                                                                                                    let nvz4 = vz4 + dz04 * m0 * mag04 + dz14 * m1 * mag14 + dz24 * m2 * mag24 + dz34 * m3 * mag34
                                                                                                                                                                                                                                                                                                                    in Sys(Body(x = x0 + dt * nvx0, y = y0 + dt * nvy0, z = z0 + dt * nvz0, vx = nvx0, vy = nvy0, vz = nvz0, mass = m0))(Body(x = x1 + dt * nvx1, y = y1 + dt * nvy1, z = z1 + dt * nvz1, vx = nvx1, vy = nvy1, vz = nvz1, mass = m1))(Body(x = x2 + dt * nvx2, y = y2 + dt * nvy2, z = z2 + dt * nvz2, vx = nvx2, vy = nvy2, vz = nvz2, mass = m2))(Body(x = x3 + dt * nvx3, y = y3 + dt * nvy3, z = z3 + dt * nvz3, vx = nvx3, vy = nvy3, vz = nvz3, mass = m3))(Body(x = x4 + dt * nvx4, y = y4 + dt * nvy4, z = z4 + dt * nvz4, vx = nvx4, vy = nvy4, vz = nvz4, mass = m4))

let energy s =
    match s with
        | Sys(b0, b1, b2, b3, b4) ->
            match b0 with
                | Body(x0, y0, z0, vx0, vy0, vz0, m0) ->
                    match b1 with
                        | Body(x1, y1, z1, vx1, vy1, vz1, m1) ->
                            match b2 with
                                | Body(x2, y2, z2, vx2, vy2, vz2, m2) ->
                                    match b3 with
                                        | Body(x3, y3, z3, vx3, vy3, vz3, m3) ->
                                            match b4 with
                                                | Body(x4, y4, z4, vx4, vy4, vz4, m4) ->
                                                    let r01 = math.sqrt((x0 - x1) * (x0 - x1) + (y0 - y1) * (y0 - y1) + (z0 - z1) * (z0 - z1))
                                                    in
                                                        let r02 = math.sqrt((x0 - x2) * (x0 - x2) + (y0 - y2) * (y0 - y2) + (z0 - z2) * (z0 - z2))
                                                        in
                                                            let r03 = math.sqrt((x0 - x3) * (x0 - x3) + (y0 - y3) * (y0 - y3) + (z0 - z3) * (z0 - z3))
                                                            in
                                                                let r04 = math.sqrt((x0 - x4) * (x0 - x4) + (y0 - y4) * (y0 - y4) + (z0 - z4) * (z0 - z4))
                                                                in
                                                                    let r12 = math.sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2) + (z1 - z2) * (z1 - z2))
                                                                    in
                                                                        let r13 = math.sqrt((x1 - x3) * (x1 - x3) + (y1 - y3) * (y1 - y3) + (z1 - z3) * (z1 - z3))
                                                                        in
                                                                            let r14 = math.sqrt((x1 - x4) * (x1 - x4) + (y1 - y4) * (y1 - y4) + (z1 - z4) * (z1 - z4))
                                                                            in
                                                                                let r23 = math.sqrt((x2 - x3) * (x2 - x3) + (y2 - y3) * (y2 - y3) + (z2 - z3) * (z2 - z3))
                                                                                in
                                                                                    let r24 = math.sqrt((x2 - x4) * (x2 - x4) + (y2 - y4) * (y2 - y4) + (z2 - z4) * (z2 - z4))
                                                                                    in
                                                                                        let r34 = math.sqrt((x3 - x4) * (x3 - x4) + (y3 - y4) * (y3 - y4) + (z3 - z4) * (z3 - z4))
                                                                                        in 0.5 * m0 * (vx0 * vx0 + vy0 * vy0 + vz0 * vz0) + 0.5 * m1 * (vx1 * vx1 + vy1 * vy1 + vz1 * vz1) + 0.5 * m2 * (vx2 * vx2 + vy2 * vy2 + vz2 * vz2) + 0.5 * m3 * (vx3 * vx3 + vy3 * vy3 + vz3 * vz3) + 0.5 * m4 * (vx4 * vx4 + vy4 * vy4 + vz4 * vz4) - m0 * m1 / r01 - m0 * m2 / r02 - m0 * m3 / r03 - m0 * m4 / r04 - m1 * m2 / r12 - m1 * m3 / r13 - m1 * m4 / r14 - m2 * m3 / r23 - m2 * m4 / r24 - m3 * m4 / r34

let recursive run n s =
    if n == 0
    then s
    else
        s
        |> advance
        |> run(n - 1)

let initial =
    neptune
    |> Sys(sun)(jupiter)(saturn)(uranus)
    |> offsetSun

let simulate n =
    (let final = run(n)(initial)
    in
        let _ =
            9
            |> text.formatFloat(energy(initial))
            |> io.print
        in
            9
            |> text.formatFloat(energy(final))
            |> io.print)

match io.args with
    | arg :: _ ->
        match text.parseInt(arg) with
            | Ok(n) -> simulate(n)
            | Error(_) -> simulate(1000)
    | [] -> simulate(1000)
