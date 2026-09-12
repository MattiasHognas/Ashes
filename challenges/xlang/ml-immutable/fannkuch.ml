(* fannkuch-redux, purely immutable: mirrors challenges/fannkuch-redux/fannkuch-redux.ash
   operation for operation -- singly linked lists, no arrays, no refs, no while loops. *)

let rec iota i n = if i > n then [] else i :: iota (i + 1) n
let rec zeros n = if n = 0 then [] else 0 :: zeros (n - 1)

let rec get_at i xs =
  match xs with [] -> 0 | h :: t -> if i = 0 then h else get_at (i - 1) t

let rec set_at i v xs =
  match xs with [] -> [] | h :: t -> if i = 0 then v :: t else h :: set_at (i - 1) v t

let rec insert_at i v xs =
  if i = 0 then v :: xs
  else match xs with [] -> [ v ] | h :: t -> h :: insert_at (i - 1) v t

let rotate_first r xs = match xs with [] -> [] | h :: t -> insert_at r h t

let rec append_tail acc xs =
  match acc with [] -> xs | h :: t -> h :: append_tail t xs

let rec flip_into k xs acc =
  if k = 0 then append_tail acc xs
  else match xs with [] -> append_tail acc xs | h :: t -> flip_into (k - 1) t (h :: acc)

let flip k xs = flip_into k xs []

let rec count_flips perm flips =
  match perm with
  | [] -> flips
  | h :: _ -> if h = 1 then flips else count_flips (flip h perm) (flips + 1)

type state = S of int list * int list
type step = Done | Continue of state * int

let rec next_perm r n st =
  if r = n then Done
  else
    match st with
    | S (perm, count) ->
        let perm2 = rotate_first r perm in
        let cr = get_at r count - 1 in
        let count2 = set_at r cr count in
        if cr > 0 then Continue (S (perm2, count2), r)
        else next_perm (r + 1) n (S (perm2, count2))

let rec reset_counts r count =
  if r = 1 then count else reset_counts (r - 1) (set_at (r - 1) r count)

let rec loop n st r sign max_flips checksum =
  match st with
  | S (perm, count) ->
      let count1 = reset_counts r count in
      let flips = count_flips perm 0 in
      let max_flips2 = if flips > max_flips then flips else max_flips in
      let checksum2 = checksum + (sign * flips) in
      (match next_perm 1 n (S (perm, count1)) with
      | Done -> (checksum2, max_flips2)
      | Continue (st2, r2) -> loop n st2 r2 (-sign) max_flips2 checksum2)

let fannkuch n = loop n (S (iota 1 n, zeros n)) n 1 0 0

let () =
  let n = if Array.length Sys.argv > 1 then int_of_string Sys.argv.(1) else 7 in
  let checksum, max_flips = fannkuch n in
  Printf.printf "%d\nPfannkuchen(%d) = %d\n" checksum n max_flips
