let pi = 3.141592653589793
let solar_mass = 4.0 *. pi *. pi
let days_per_year = 365.24

type body = { mutable x: float; mutable y: float; mutable z: float;
              mutable vx: float; mutable vy: float; mutable vz: float; mass: float }

let make_bodies () = [|
  { x = 0.0; y = 0.0; z = 0.0; vx = 0.0; vy = 0.0; vz = 0.0; mass = solar_mass };
  { x = 4.84143144246472090; y = -1.16032004402742839; z = -0.103622044471123109;
    vx = 0.00166007664274403694 *. days_per_year; vy = 0.00769901118419740425 *. days_per_year;
    vz = -0.0000690460016972063023 *. days_per_year; mass = 0.000954791938424326609 *. solar_mass };
  { x = 8.34336671824457987; y = 4.12479856412430479; z = -0.403523417114321381;
    vx = -0.00276742510726862411 *. days_per_year; vy = 0.00499852801234917238 *. days_per_year;
    vz = 0.0000230417297573763929 *. days_per_year; mass = 0.000285885980666130812 *. solar_mass };
  { x = 12.8943695621391310; y = -15.1111514016986312; z = -0.223307578892655734;
    vx = 0.00296460137564761618 *. days_per_year; vy = 0.00237847173959480950 *. days_per_year;
    vz = -0.0000296589568540237556 *. days_per_year; mass = 0.0000436624404335156298 *. solar_mass };
  { x = 15.3796971148509165; y = -25.9193146099879641; z = 0.179258772950371181;
    vx = 0.00268067772490389322 *. days_per_year; vy = 0.00162824170038242295 *. days_per_year;
    vz = -0.0000951592254519715870 *. days_per_year; mass = 0.0000515138902046611451 *. solar_mass };
|]

let offset_momentum b =
  let px = ref 0.0 and py = ref 0.0 and pz = ref 0.0 in
  Array.iter (fun x ->
    px := !px +. x.vx *. x.mass;
    py := !py +. x.vy *. x.mass;
    pz := !pz +. x.vz *. x.mass) b;
  b.(0).vx <- -. !px /. solar_mass;
  b.(0).vy <- -. !py /. solar_mass;
  b.(0).vz <- -. !pz /. solar_mass

let advance b dt =
  for i = 0 to 4 do
    for j = i + 1 to 4 do
      let bi = b.(i) and bj = b.(j) in
      let dx = bi.x -. bj.x and dy = bi.y -. bj.y and dz = bi.z -. bj.z in
      let d2 = dx *. dx +. dy *. dy +. dz *. dz in
      let mag = dt /. (d2 *. sqrt d2) in
      let mj = bj.mass *. mag and mi = bi.mass *. mag in
      bi.vx <- bi.vx -. dx *. mj; bi.vy <- bi.vy -. dy *. mj; bi.vz <- bi.vz -. dz *. mj;
      bj.vx <- bj.vx +. dx *. mi; bj.vy <- bj.vy +. dy *. mi; bj.vz <- bj.vz +. dz *. mi
    done
  done;
  Array.iter (fun x ->
    x.x <- x.x +. dt *. x.vx;
    x.y <- x.y +. dt *. x.vy;
    x.z <- x.z +. dt *. x.vz) b

let energy b =
  let e = ref 0.0 in
  for i = 0 to 4 do
    let bi = b.(i) in
    e := !e +. 0.5 *. bi.mass *. (bi.vx *. bi.vx +. bi.vy *. bi.vy +. bi.vz *. bi.vz);
    for j = i + 1 to 4 do
      let bj = b.(j) in
      let dx = bi.x -. bj.x and dy = bi.y -. bj.y and dz = bi.z -. bj.z in
      e := !e -. bi.mass *. bj.mass /. sqrt (dx *. dx +. dy *. dy +. dz *. dz)
    done
  done;
  !e

let () =
  let n = if Array.length Sys.argv > 1 then int_of_string Sys.argv.(1) else 1000 in
  let b = make_bodies () in
  offset_momentum b;
  Printf.printf "%.9f\n" (energy b);
  for _ = 1 to n do advance b 0.01 done;
  Printf.printf "%.9f\n" (energy b)
