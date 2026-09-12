type tree = Leaf | Node of tree * tree

let rec make depth = if depth = 0 then Leaf else Node (make (depth - 1), make (depth - 1))
let rec check t = match t with Leaf -> 1 | Node (l, r) -> 1 + check l + check r

let () =
  let n = if Array.length Sys.argv > 1 then int_of_string Sys.argv.(1) else 10 in
  let min_depth = 4 in
  let max_depth = if min_depth + 2 > n then min_depth + 2 else n in
  let stretch_depth = max_depth + 1 in
  let long_lived = make max_depth in
  let buf = Buffer.create 1024 in
  Buffer.add_string buf (Printf.sprintf "stretch tree of depth %d\t check: %d\n" stretch_depth (check (make stretch_depth)));
  let depth = ref min_depth in
  while !depth <= max_depth do
    let iterations = 1 lsl (max_depth - !depth + min_depth) in
    let sum = ref 0 in
    for _ = 1 to iterations do sum := !sum + check (make !depth) done;
    Buffer.add_string buf (Printf.sprintf "%d\t trees of depth %d\t check: %d\n" iterations !depth !sum);
    depth := !depth + 2
  done;
  Buffer.add_string buf (Printf.sprintf "long lived tree of depth %d\t check: %d\n" max_depth (check long_lived));
  print_string (Buffer.contents buf)
