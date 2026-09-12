(* n-body with the pairwise interactions unrolled, matching the shape of the Ashes program:
   five named bodies, ten explicit pair blocks, no loop over pairs. *)
let pi = 3.141592653589793
let solar_mass = 4.0 *. pi *. pi
let days_per_year = 365.24

type body = { mutable x: float; mutable y: float; mutable z: float;
              mutable vx: float; mutable vy: float; mutable vz: float; mass: float }

let b0 = { x = 0.0; y = 0.0; z = 0.0; vx = 0.0; vy = 0.0; vz = 0.0; mass = solar_mass }
let b1 = { x = 4.84143144246472090; y = -1.16032004402742839; z = -0.103622044471123109;
  vx = 0.00166007664274403694 *. days_per_year; vy = 0.00769901118419740425 *. days_per_year;
  vz = -0.0000690460016972063023 *. days_per_year; mass = 0.000954791938424326609 *. solar_mass }
let b2 = { x = 8.34336671824457987; y = 4.12479856412430479; z = -0.403523417114321381;
  vx = -0.00276742510726862411 *. days_per_year; vy = 0.00499852801234917238 *. days_per_year;
  vz = 0.0000230417297573763929 *. days_per_year; mass = 0.000285885980666130812 *. solar_mass }
let b3 = { x = 12.8943695621391310; y = -15.1111514016986312; z = -0.223307578892655734;
  vx = 0.00296460137564761618 *. days_per_year; vy = 0.00237847173959480950 *. days_per_year;
  vz = -0.0000296589568540237556 *. days_per_year; mass = 0.0000436624404335156298 *. solar_mass }
let b4 = { x = 15.3796971148509165; y = -25.9193146099879641; z = 0.179258772950371181;
  vx = 0.00268067772490389322 *. days_per_year; vy = 0.00162824170038242295 *. days_per_year;
  vz = -0.0000951592254519715870 *. days_per_year; mass = 0.0000515138902046611451 *. solar_mass }

let offset_momentum () =
  let px = b0.vx*.b0.mass +. b1.vx*.b1.mass +. b2.vx*.b2.mass +. b3.vx*.b3.mass +. b4.vx*.b4.mass in
  let py = b0.vy*.b0.mass +. b1.vy*.b1.mass +. b2.vy*.b2.mass +. b3.vy*.b3.mass +. b4.vy*.b4.mass in
  let pz = b0.vz*.b0.mass +. b1.vz*.b1.mass +. b2.vz*.b2.mass +. b3.vz*.b3.mass +. b4.vz*.b4.mass in
  b0.vx <- -. px /. solar_mass; b0.vy <- -. py /. solar_mass; b0.vz <- -. pz /. solar_mass

let advance dt =
  let dx01 = b0.x -. b1.x and dy01 = b0.y -. b1.y and dz01 = b0.z -. b1.z in
  let d201 = dx01*.dx01 +. dy01*.dy01 +. dz01*.dz01 in
  let mag01 = dt /. (d201 *. sqrt d201) in
  let dx02 = b0.x -. b2.x and dy02 = b0.y -. b2.y and dz02 = b0.z -. b2.z in
  let d202 = dx02*.dx02 +. dy02*.dy02 +. dz02*.dz02 in
  let mag02 = dt /. (d202 *. sqrt d202) in
  let dx03 = b0.x -. b3.x and dy03 = b0.y -. b3.y and dz03 = b0.z -. b3.z in
  let d203 = dx03*.dx03 +. dy03*.dy03 +. dz03*.dz03 in
  let mag03 = dt /. (d203 *. sqrt d203) in
  let dx04 = b0.x -. b4.x and dy04 = b0.y -. b4.y and dz04 = b0.z -. b4.z in
  let d204 = dx04*.dx04 +. dy04*.dy04 +. dz04*.dz04 in
  let mag04 = dt /. (d204 *. sqrt d204) in
  let dx12 = b1.x -. b2.x and dy12 = b1.y -. b2.y and dz12 = b1.z -. b2.z in
  let d212 = dx12*.dx12 +. dy12*.dy12 +. dz12*.dz12 in
  let mag12 = dt /. (d212 *. sqrt d212) in
  let dx13 = b1.x -. b3.x and dy13 = b1.y -. b3.y and dz13 = b1.z -. b3.z in
  let d213 = dx13*.dx13 +. dy13*.dy13 +. dz13*.dz13 in
  let mag13 = dt /. (d213 *. sqrt d213) in
  let dx14 = b1.x -. b4.x and dy14 = b1.y -. b4.y and dz14 = b1.z -. b4.z in
  let d214 = dx14*.dx14 +. dy14*.dy14 +. dz14*.dz14 in
  let mag14 = dt /. (d214 *. sqrt d214) in
  let dx23 = b2.x -. b3.x and dy23 = b2.y -. b3.y and dz23 = b2.z -. b3.z in
  let d223 = dx23*.dx23 +. dy23*.dy23 +. dz23*.dz23 in
  let mag23 = dt /. (d223 *. sqrt d223) in
  let dx24 = b2.x -. b4.x and dy24 = b2.y -. b4.y and dz24 = b2.z -. b4.z in
  let d224 = dx24*.dx24 +. dy24*.dy24 +. dz24*.dz24 in
  let mag24 = dt /. (d224 *. sqrt d224) in
  let dx34 = b3.x -. b4.x and dy34 = b3.y -. b4.y and dz34 = b3.z -. b4.z in
  let d234 = dx34*.dx34 +. dy34*.dy34 +. dz34*.dz34 in
  let mag34 = dt /. (d234 *. sqrt d234) in
  let nvx0 = b0.vx -. dx01 *. b1.mass *. mag01 -. dx02 *. b2.mass *. mag02 -. dx03 *. b3.mass *. mag03 -. dx04 *. b4.mass *. mag04 in
  let nvx1 = b1.vx +. dx01 *. b0.mass *. mag01 -. dx12 *. b2.mass *. mag12 -. dx13 *. b3.mass *. mag13 -. dx14 *. b4.mass *. mag14 in
  let nvx2 = b2.vx +. dx02 *. b0.mass *. mag02 +. dx12 *. b1.mass *. mag12 -. dx23 *. b3.mass *. mag23 -. dx24 *. b4.mass *. mag24 in
  let nvx3 = b3.vx +. dx03 *. b0.mass *. mag03 +. dx13 *. b1.mass *. mag13 +. dx23 *. b2.mass *. mag23 -. dx34 *. b4.mass *. mag34 in
  let nvx4 = b4.vx +. dx04 *. b0.mass *. mag04 +. dx14 *. b1.mass *. mag14 +. dx24 *. b2.mass *. mag24 +. dx34 *. b3.mass *. mag34 in
  let nvy0 = b0.vy -. dy01 *. b1.mass *. mag01 -. dy02 *. b2.mass *. mag02 -. dy03 *. b3.mass *. mag03 -. dy04 *. b4.mass *. mag04 in
  let nvy1 = b1.vy +. dy01 *. b0.mass *. mag01 -. dy12 *. b2.mass *. mag12 -. dy13 *. b3.mass *. mag13 -. dy14 *. b4.mass *. mag14 in
  let nvy2 = b2.vy +. dy02 *. b0.mass *. mag02 +. dy12 *. b1.mass *. mag12 -. dy23 *. b3.mass *. mag23 -. dy24 *. b4.mass *. mag24 in
  let nvy3 = b3.vy +. dy03 *. b0.mass *. mag03 +. dy13 *. b1.mass *. mag13 +. dy23 *. b2.mass *. mag23 -. dy34 *. b4.mass *. mag34 in
  let nvy4 = b4.vy +. dy04 *. b0.mass *. mag04 +. dy14 *. b1.mass *. mag14 +. dy24 *. b2.mass *. mag24 +. dy34 *. b3.mass *. mag34 in
  let nvz0 = b0.vz -. dz01 *. b1.mass *. mag01 -. dz02 *. b2.mass *. mag02 -. dz03 *. b3.mass *. mag03 -. dz04 *. b4.mass *. mag04 in
  let nvz1 = b1.vz +. dz01 *. b0.mass *. mag01 -. dz12 *. b2.mass *. mag12 -. dz13 *. b3.mass *. mag13 -. dz14 *. b4.mass *. mag14 in
  let nvz2 = b2.vz +. dz02 *. b0.mass *. mag02 +. dz12 *. b1.mass *. mag12 -. dz23 *. b3.mass *. mag23 -. dz24 *. b4.mass *. mag24 in
  let nvz3 = b3.vz +. dz03 *. b0.mass *. mag03 +. dz13 *. b1.mass *. mag13 +. dz23 *. b2.mass *. mag23 -. dz34 *. b4.mass *. mag34 in
  let nvz4 = b4.vz +. dz04 *. b0.mass *. mag04 +. dz14 *. b1.mass *. mag14 +. dz24 *. b2.mass *. mag24 +. dz34 *. b3.mass *. mag34 in
  b0.vx <- nvx0; b0.vy <- nvy0; b0.vz <- nvz0;
  b1.vx <- nvx1; b1.vy <- nvy1; b1.vz <- nvz1;
  b2.vx <- nvx2; b2.vy <- nvy2; b2.vz <- nvz2;
  b3.vx <- nvx3; b3.vy <- nvy3; b3.vz <- nvz3;
  b4.vx <- nvx4; b4.vy <- nvy4; b4.vz <- nvz4;
  b0.x <- b0.x +. dt *. b0.vx; b0.y <- b0.y +. dt *. b0.vy; b0.z <- b0.z +. dt *. b0.vz;
  b1.x <- b1.x +. dt *. b1.vx; b1.y <- b1.y +. dt *. b1.vy; b1.z <- b1.z +. dt *. b1.vz;
  b2.x <- b2.x +. dt *. b2.vx; b2.y <- b2.y +. dt *. b2.vy; b2.z <- b2.z +. dt *. b2.vz;
  b3.x <- b3.x +. dt *. b3.vx; b3.y <- b3.y +. dt *. b3.vy; b3.z <- b3.z +. dt *. b3.vz;
  b4.x <- b4.x +. dt *. b4.vx; b4.y <- b4.y +. dt *. b4.vy; b4.z <- b4.z +. dt *. b4.vz;
  ()

let energy () =
  let e = ref 0.0 in
  List.iter (fun b -> e := !e +. 0.5 *. b.mass *. (b.vx*.b.vx +. b.vy*.b.vy +. b.vz*.b.vz))
    [b0; b1; b2; b3; b4];
  let pot a b =
    let dx = a.x -. b.x and dy = a.y -. b.y and dz = a.z -. b.z in
    e := !e -. a.mass *. b.mass /. sqrt (dx*.dx +. dy*.dy +. dz*.dz)
  in
  pot b0 b1; pot b0 b2; pot b0 b3; pot b0 b4; pot b1 b2; pot b1 b3; pot b1 b4;
  pot b2 b3; pot b2 b4; pot b3 b4;
  !e

let () =
  let n = if Array.length Sys.argv > 1 then int_of_string Sys.argv.(1) else 1000 in
  offset_momentum ();
  Printf.printf "%.9f\n" (energy ());
  for _ = 1 to n do advance 0.01 done;
  Printf.printf "%.9f\n" (energy ())
