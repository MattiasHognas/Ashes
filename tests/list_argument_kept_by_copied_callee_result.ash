// expect: 533334
// A runtime-managed list passed by name to a callee whose result may keep it (flip returns the
// un-flipped suffix of its argument) is retained for that result; the caller then copies the
// list result out, so nothing of the argument survives in it and the retained reference must be
// released after the call. Every flip leaked a whole permutation before.
let recursive appendTail acc xs =
    match acc with
        | [] -> xs
        | h :: t -> h :: appendTail(t)(xs)

let recursive flipInto k xs acc =
    if k == 0
    then appendTail(acc)(xs)
    else
        match xs with
            | [] -> appendTail(acc)(xs)
            | h :: t -> flipInto(k - 1)(t)(h :: acc)

let flip k xs = flipInto(k)(xs)([])

let recursive countFlips perm flips =
    match perm with
        | [] -> flips
        | h :: _ ->
            if h == 1
            then flips
            else
                countFlips(flip(h)(perm))(flips + 1)

let build i =
    match i % 3 with
        | 0 -> [3, 1, 2, 6, 5, 4]
        | 1 -> [4, 6, 2, 1, 3, 5]
        | _ -> [6, 2, 4, 5, 1, 3]

let recursive run i total =
    if i == 0
    then total
    else
        run(i - 1)(total + countFlips(build(i))(0))

0
|> run(200000)
|> Ashes.Text.fromInt
|> Ashes.IO.print
