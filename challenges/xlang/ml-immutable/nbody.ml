(* n-body, purely immutable: mirrors challenges/n-body/n-body.ash -- immutable records, a fixed
   five-body system rebuilt every step, ten pair interactions. No refs, arrays, loops or mutation. *)
let pi = 3.141592653589793
let solar_mass = 4.0 *. pi *. pi
let days_per_year = 365.24

type body = { x: float; y: float; z: float; vx: float; vy: float; vz: float; mass: float }
type system = { b0: body; b1: body; b2: body; b3: body; b4: body }

let initial_sys = {
  b0 = { x = 0.0; y = 0.0; z = 0.0; vx = 0.0; vy = 0.0; vz = 0.0; mass = solar_mass };
  b1 = { x = 4.84143144246472090; y = -1.16032004402742839; z = -0.103622044471123109;
    vx = 0.00166007664274403694 *. days_per_year; vy = 0.00769901118419740425 *. days_per_year;
    vz = -0.0000690460016972063023 *. days_per_year; mass = 0.000954791938424326609 *. solar_mass };
  b2 = { x = 8.34336671824457987; y = 4.12479856412430479; z = -0.403523417114321381;
    vx = -0.00276742510726862411 *. days_per_year; vy = 0.00499852801234917238 *. days_per_year;
    vz = 0.0000230417297573763929 *. days_per_year; mass = 0.000285885980666130812 *. solar_mass };
  b3 = { x = 12.8943695621391310; y = -15.1111514016986312; z = -0.223307578892655734;
    vx = 0.00296460137564761618 *. days_per_year; vy = 0.00237847173959480950 *. days_per_year;
    vz = -0.0000296589568540237556 *. days_per_year; mass = 0.0000436624404335156298 *. solar_mass };
  b4 = { x = 15.3796971148509165; y = -25.9193146099879641; z = 0.179258772950371181;
    vx = 0.00268067772490389322 *. days_per_year; vy = 0.00162824170038242295 *. days_per_year;
    vz = -0.0000951592254519715870 *. days_per_year; mass = 0.0000515138902046611451 *. solar_mass };
}

let offset_sun s =
  let px = (s.b0.vx *. s.b0.mass) +. (s.b1.vx *. s.b1.mass) +. (s.b2.vx *. s.b2.mass)
           +. (s.b3.vx *. s.b3.mass) +. (s.b4.vx *. s.b4.mass) in
  let py = (s.b0.vy *. s.b0.mass) +. (s.b1.vy *. s.b1.mass) +. (s.b2.vy *. s.b2.mass)
           +. (s.b3.vy *. s.b3.mass) +. (s.b4.vy *. s.b4.mass) in
  let pz = (s.b0.vz *. s.b0.mass) +. (s.b1.vz *. s.b1.mass) +. (s.b2.vz *. s.b2.mass)
           +. (s.b3.vz *. s.b3.mass) +. (s.b4.vz *. s.b4.mass) in
  { s with b0 = { (s.b0) with vx = -. px /. solar_mass; vy = -. py /. solar_mass;
                              vz = -. pz /. solar_mass } }

let advance dt s =
  let x0 = s.b0.x and y0 = s.b0.y and z0 = s.b0.z in
  let vx0 = s.b0.vx and vy0 = s.b0.vy and vz0 = s.b0.vz and m0 = s.b0.mass in
  let x1 = s.b1.x and y1 = s.b1.y and z1 = s.b1.z in
  let vx1 = s.b1.vx and vy1 = s.b1.vy and vz1 = s.b1.vz and m1 = s.b1.mass in
  let x2 = s.b2.x and y2 = s.b2.y and z2 = s.b2.z in
  let vx2 = s.b2.vx and vy2 = s.b2.vy and vz2 = s.b2.vz and m2 = s.b2.mass in
  let x3 = s.b3.x and y3 = s.b3.y and z3 = s.b3.z in
  let vx3 = s.b3.vx and vy3 = s.b3.vy and vz3 = s.b3.vz and m3 = s.b3.mass in
  let x4 = s.b4.x and y4 = s.b4.y and z4 = s.b4.z in
  let vx4 = s.b4.vx and vy4 = s.b4.vy and vz4 = s.b4.vz and m4 = s.b4.mass in
  let dx01 = x0 -. x1 and dy01 = y0 -. y1 and dz01 = z0 -. z1 in
  let d201 = (dx01 *. dx01) +. (dy01 *. dy01) +. (dz01 *. dz01) in
  let mag01 = dt /. (d201 *. sqrt d201) in
  let dx02 = x0 -. x2 and dy02 = y0 -. y2 and dz02 = z0 -. z2 in
  let d202 = (dx02 *. dx02) +. (dy02 *. dy02) +. (dz02 *. dz02) in
  let mag02 = dt /. (d202 *. sqrt d202) in
  let dx03 = x0 -. x3 and dy03 = y0 -. y3 and dz03 = z0 -. z3 in
  let d203 = (dx03 *. dx03) +. (dy03 *. dy03) +. (dz03 *. dz03) in
  let mag03 = dt /. (d203 *. sqrt d203) in
  let dx04 = x0 -. x4 and dy04 = y0 -. y4 and dz04 = z0 -. z4 in
  let d204 = (dx04 *. dx04) +. (dy04 *. dy04) +. (dz04 *. dz04) in
  let mag04 = dt /. (d204 *. sqrt d204) in
  let dx12 = x1 -. x2 and dy12 = y1 -. y2 and dz12 = z1 -. z2 in
  let d212 = (dx12 *. dx12) +. (dy12 *. dy12) +. (dz12 *. dz12) in
  let mag12 = dt /. (d212 *. sqrt d212) in
  let dx13 = x1 -. x3 and dy13 = y1 -. y3 and dz13 = z1 -. z3 in
  let d213 = (dx13 *. dx13) +. (dy13 *. dy13) +. (dz13 *. dz13) in
  let mag13 = dt /. (d213 *. sqrt d213) in
  let dx14 = x1 -. x4 and dy14 = y1 -. y4 and dz14 = z1 -. z4 in
  let d214 = (dx14 *. dx14) +. (dy14 *. dy14) +. (dz14 *. dz14) in
  let mag14 = dt /. (d214 *. sqrt d214) in
  let dx23 = x2 -. x3 and dy23 = y2 -. y3 and dz23 = z2 -. z3 in
  let d223 = (dx23 *. dx23) +. (dy23 *. dy23) +. (dz23 *. dz23) in
  let mag23 = dt /. (d223 *. sqrt d223) in
  let dx24 = x2 -. x4 and dy24 = y2 -. y4 and dz24 = z2 -. z4 in
  let d224 = (dx24 *. dx24) +. (dy24 *. dy24) +. (dz24 *. dz24) in
  let mag24 = dt /. (d224 *. sqrt d224) in
  let dx34 = x3 -. x4 and dy34 = y3 -. y4 and dz34 = z3 -. z4 in
  let d234 = (dx34 *. dx34) +. (dy34 *. dy34) +. (dz34 *. dz34) in
  let mag34 = dt /. (d234 *. sqrt d234) in
  let nvx0 = vx0 -. (dx01 *. m1 *. mag01) -. (dx02 *. m2 *. mag02) -. (dx03 *. m3 *. mag03) -. (dx04 *. m4 *. mag04) in
  let nvx1 = vx1 +. (dx01 *. m0 *. mag01) -. (dx12 *. m2 *. mag12) -. (dx13 *. m3 *. mag13) -. (dx14 *. m4 *. mag14) in
  let nvx2 = vx2 +. (dx02 *. m0 *. mag02) +. (dx12 *. m1 *. mag12) -. (dx23 *. m3 *. mag23) -. (dx24 *. m4 *. mag24) in
  let nvx3 = vx3 +. (dx03 *. m0 *. mag03) +. (dx13 *. m1 *. mag13) +. (dx23 *. m2 *. mag23) -. (dx34 *. m4 *. mag34) in
  let nvx4 = vx4 +. (dx04 *. m0 *. mag04) +. (dx14 *. m1 *. mag14) +. (dx24 *. m2 *. mag24) +. (dx34 *. m3 *. mag34) in
  let nvy0 = vy0 -. (dy01 *. m1 *. mag01) -. (dy02 *. m2 *. mag02) -. (dy03 *. m3 *. mag03) -. (dy04 *. m4 *. mag04) in
  let nvy1 = vy1 +. (dy01 *. m0 *. mag01) -. (dy12 *. m2 *. mag12) -. (dy13 *. m3 *. mag13) -. (dy14 *. m4 *. mag14) in
  let nvy2 = vy2 +. (dy02 *. m0 *. mag02) +. (dy12 *. m1 *. mag12) -. (dy23 *. m3 *. mag23) -. (dy24 *. m4 *. mag24) in
  let nvy3 = vy3 +. (dy03 *. m0 *. mag03) +. (dy13 *. m1 *. mag13) +. (dy23 *. m2 *. mag23) -. (dy34 *. m4 *. mag34) in
  let nvy4 = vy4 +. (dy04 *. m0 *. mag04) +. (dy14 *. m1 *. mag14) +. (dy24 *. m2 *. mag24) +. (dy34 *. m3 *. mag34) in
  let nvz0 = vz0 -. (dz01 *. m1 *. mag01) -. (dz02 *. m2 *. mag02) -. (dz03 *. m3 *. mag03) -. (dz04 *. m4 *. mag04) in
  let nvz1 = vz1 +. (dz01 *. m0 *. mag01) -. (dz12 *. m2 *. mag12) -. (dz13 *. m3 *. mag13) -. (dz14 *. m4 *. mag14) in
  let nvz2 = vz2 +. (dz02 *. m0 *. mag02) +. (dz12 *. m1 *. mag12) -. (dz23 *. m3 *. mag23) -. (dz24 *. m4 *. mag24) in
  let nvz3 = vz3 +. (dz03 *. m0 *. mag03) +. (dz13 *. m1 *. mag13) +. (dz23 *. m2 *. mag23) -. (dz34 *. m4 *. mag34) in
  let nvz4 = vz4 +. (dz04 *. m0 *. mag04) +. (dz14 *. m1 *. mag14) +. (dz24 *. m2 *. mag24) +. (dz34 *. m3 *. mag34) in
  { b0 = { x = x0 +. (dt *. nvx0); y = y0 +. (dt *. nvy0); z = z0 +. (dt *. nvz0); vx = nvx0; vy = nvy0; vz = nvz0; mass = m0 }; b1 = { x = x1 +. (dt *. nvx1); y = y1 +. (dt *. nvy1); z = z1 +. (dt *. nvz1); vx = nvx1; vy = nvy1; vz = nvz1; mass = m1 }; b2 = { x = x2 +. (dt *. nvx2); y = y2 +. (dt *. nvy2); z = z2 +. (dt *. nvz2); vx = nvx2; vy = nvy2; vz = nvz2; mass = m2 }; b3 = { x = x3 +. (dt *. nvx3); y = y3 +. (dt *. nvy3); z = z3 +. (dt *. nvz3); vx = nvx3; vy = nvy3; vz = nvz3; mass = m3 }; b4 = { x = x4 +. (dt *. nvx4); y = y4 +. (dt *. nvy4); z = z4 +. (dt *. nvz4); vx = nvx4; vy = nvy4; vz = nvz4; mass = m4 }; }

let energy s =
  let bs = [ s.b0; s.b1; s.b2; s.b3; s.b4 ] in
  let rec kin l acc = match l with
    | [] -> acc
    | b :: r -> kin r (acc +. (0.5 *. b.mass *. ((b.vx *. b.vx) +. (b.vy *. b.vy) +. (b.vz *. b.vz)))) in
  let rec pot l acc = match l with
    | [] -> acc
    | b :: r ->
        let rec against l2 a2 = match l2 with
          | [] -> a2
          | o :: r2 ->
              let dx = b.x -. o.x and dy = b.y -. o.y and dz = b.z -. o.z in
              against r2 (a2 -. (b.mass *. o.mass /. sqrt ((dx *. dx) +. (dy *. dy) +. (dz *. dz)))) in
        pot r (against r acc) in
  pot bs (kin bs 0.0)

let rec run n dt s = if n = 0 then s else run (n - 1) dt (advance dt s)

let () =
  let n = if Array.length Sys.argv > 1 then int_of_string Sys.argv.(1) else 1000 in
  let s = offset_sun initial_sys in
  Printf.printf "%.9f\n" (energy s);
  Printf.printf "%.9f\n" (energy (run n 0.01 s))
