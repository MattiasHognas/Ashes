// expect: 1020
// A loop returns a let-bound call result that may be its own list parameter handed straight
// through: bump returns its argument unchanged when the order holds no negative entry. The result
// holds a reference of its own, so the exit releases the parameter instead of transferring its
// reference to the equal result.
let recursive bump (order: List(Int)) (xs: List(Int)) =
    match order with
        | [] -> xs
        | o :: rest ->
            if o < 0
            then bump(rest)(o :: xs)
            else bump(rest)(xs)

let recursive settle (k: Int) (xs: List(Int)) =
    (let next = bump([1, 2])(xs)
    in
        if k == 0
        then next
        else settle(k - 1)(next))

let recursive sum (xs: List(Int)) =
    match xs with
        | [] -> 0
        | x :: rest -> x + sum(rest)

let probe (n: Int) =
    [n, 2, 3]
    |> settle(1)
    |> sum

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(40)
|> Ashes.IO.print
