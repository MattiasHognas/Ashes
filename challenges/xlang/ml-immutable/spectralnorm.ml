(* spectral-norm, purely immutable: mirrors challenges/spectral-norm/spectral-norm.ash
   operation for operation -- lists and recursion, no arrays, no refs, no loops. *)
let a i j = 1.0 /. float_of_int (((i + j) * (i + j + 1) / 2) + i + 1)

let rec ones i acc = if i = 0 then acc else ones (i - 1) (1.0 :: acc)

let rec av_row i j v acc =
  match v with [] -> acc | x :: rest -> av_row i (j + 1) rest ((a i j *. x) +. acc)

let rec at_row i j v acc =
  match v with [] -> acc | x :: rest -> at_row i (j + 1) rest ((a j i *. x) +. acc)

let rec mul_av i v acc = if i < 0 then acc else mul_av (i - 1) v (av_row i 0 v 0.0 :: acc)
let rec mul_atv i v acc = if i < 0 then acc else mul_atv (i - 1) v (at_row i 0 v 0.0 :: acc)
let mul_atav u n = mul_atv (n - 1) (mul_av (n - 1) u []) []

let rec dot2 xs ys acc =
  match xs with
  | [] -> acc
  | x :: xr -> (match ys with [] -> acc | y :: yr -> dot2 xr yr ((x *. y) +. acc))

let rec power_loop k u v n =
  if k = 0 then sqrt (dot2 u v 0.0 /. dot2 v v 0.0)
  else
    let v2 = mul_atav u n in
    let u2 = mul_atav v2 n in
    power_loop (k - 1) u2 v2 n

let spectral_norm n = let u0 = ones n [] in power_loop 10 u0 u0 n

let () =
  let n = if Array.length Sys.argv > 1 then int_of_string Sys.argv.(1) else 100 in
  Printf.printf "%.9f\n" (spectral_norm n)
