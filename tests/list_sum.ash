// expect: 6
let recursive sum xs =
    match xs with
        | [] -> 0
        | x :: rest -> x + sum(rest)
in Ashes.IO.print(sum([1, 2, 3]))
