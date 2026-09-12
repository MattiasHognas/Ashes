let fannkuch n =
  let perm = Array.make n 0 in
  let perm1 = Array.init n (fun i -> i) in
  let count = Array.make n 0 in
  let max_flips = ref 0 and checksum = ref 0 and perm_count = ref 0 in
  let r = ref n in
  let result = ref (0, 0) in
  let finished = ref false in
  while not !finished do
    while !r <> 1 do count.(!r - 1) <- !r; decr r done;
    Array.blit perm1 0 perm 0 n;
    let flips = ref 0 in
    let continue_flip = ref true in
    while !continue_flip do
      let k = perm.(0) in
      if k = 0 then continue_flip := false
      else begin
        let k2 = (k + 1) lsr 1 in
        for i = 0 to k2 - 1 do
          let t = perm.(i) in perm.(i) <- perm.(k - i); perm.(k - i) <- t
        done;
        incr flips
      end
    done;
    if !flips > !max_flips then max_flips := !flips;
    if !perm_count mod 2 = 0 then checksum := !checksum + !flips else checksum := !checksum - !flips;
    let advancing = ref true in
    while !advancing && not !finished do
      if !r = n then begin result := (!checksum, !max_flips); finished := true end
      else begin
        let perm0 = perm1.(0) in
        let i = ref 0 in
        while !i < !r do perm1.(!i) <- perm1.(!i + 1); incr i done;
        perm1.(!r) <- perm0;
        count.(!r) <- count.(!r) - 1;
        if count.(!r) > 0 then advancing := false else incr r
      end
    done;
    incr perm_count
  done;
  !result

let () =
  let n = if Array.length Sys.argv > 1 then int_of_string Sys.argv.(1) else 7 in
  let (checksum, max_flips) = fannkuch n in
  Printf.printf "%d\nPfannkuchen(%d) = %d\n" checksum n max_flips
