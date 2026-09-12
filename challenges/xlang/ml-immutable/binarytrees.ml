(* binary-trees, purely immutable: mirrors challenges/binary-trees/binary-trees.ash --
   recursion and an accumulated string, no refs, no loops, no mutable state. *)
type tree = Leaf | Node of tree * tree

let rec make depth = if depth = 0 then Leaf else Node (make (depth - 1), make (depth - 1))
let rec check t = match t with Leaf -> 1 | Node (l, r) -> 1 + check l + check r
let rec pow2 k = if k = 0 then 1 else 2 * pow2 (k - 1)
let rec sum_checks depth n acc = if n = 0 then acc else sum_checks depth (n - 1) (check (make depth) + acc)

let rec depth_loop depth max_depth min_depth out =
  if depth > max_depth then out
  else
    let iterations = pow2 (max_depth - depth + min_depth) in
    let sum = sum_checks depth iterations 0 in
    let line =
      string_of_int iterations ^ "\t trees of depth " ^ string_of_int depth
      ^ "\t check: " ^ string_of_int sum ^ "\n"
    in
    depth_loop (depth + 2) max_depth min_depth (out ^ line)

let run n =
  let min_depth = 4 in
  let max_depth = if min_depth + 2 > n then min_depth + 2 else n in
  let stretch_depth = max_depth + 1 in
  let long_lived = make max_depth in
  let stretch_line =
    "stretch tree of depth " ^ string_of_int stretch_depth ^ "\t check: "
    ^ string_of_int (check (make stretch_depth)) ^ "\n"
  in
  let body = depth_loop min_depth max_depth min_depth "" in
  let long_line =
    "long lived tree of depth " ^ string_of_int max_depth ^ "\t check: "
    ^ string_of_int (check long_lived) ^ "\n"
  in
  stretch_line ^ body ^ long_line

let () =
  let n = if Array.length Sys.argv > 1 then int_of_string Sys.argv.(1) else 10 in
  print_string (run n)
