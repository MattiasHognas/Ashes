let a i j = 1.0 /. float_of_int ((i + j) * (i + j + 1) / 2 + i + 1)

let mul_av v out n =
  for i = 0 to n - 1 do
    let s = ref 0.0 in
    for j = 0 to n - 1 do s := !s +. a i j *. v.(j) done;
    out.(i) <- !s
  done

let mul_atv v out n =
  for i = 0 to n - 1 do
    let s = ref 0.0 in
    for j = 0 to n - 1 do s := !s +. a j i *. v.(j) done;
    out.(i) <- !s
  done

let mul_atav v out tmp n = mul_av v tmp n; mul_atv tmp out n

let () =
  let n = if Array.length Sys.argv > 1 then int_of_string Sys.argv.(1) else 100 in
  let u = Array.make n 1.0 and v = Array.make n 0.0 and tmp = Array.make n 0.0 in
  for _ = 1 to 10 do mul_atav u v tmp n; mul_atav v u tmp n done;
  let vbv = ref 0.0 and vv = ref 0.0 in
  for i = 0 to n - 1 do
    vbv := !vbv +. u.(i) *. v.(i);
    vv := !vv +. v.(i) *. v.(i)
  done;
  Printf.printf "%.9f\n" (sqrt (!vbv /. !vv))
