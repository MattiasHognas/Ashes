// expect: 1,2,3, a,b,c, 1:6 1000:6000 10000:60000
// A function returning a cons chain ending in a literal (`1 :: 2 :: 3 :: []`) builds every cell of
// the chain on the reference-counted heap, not only the first: the caller resets the call's arena
// window once the result is in hand, so an arena tail cell would be gone while the head still
// points at it. At 1, 1,000 and 10,000 calls.
let recursive showInts xs =
    match xs with
        | [] -> ""
        | h :: t -> Ashes.Text.fromInt(h) + "," + showInts(t)

let recursive showStrs xs =
    match xs with
        | [] -> ""
        | h :: t -> h + "," + showStrs(t)

let recursive sum xs =
    match xs with
        | [] -> 0
        | h :: t -> h + sum(t)

let ints x = 1 :: 2 :: x :: []

let strs x = "a" :: "b" :: x :: []

let recursive rounds (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        rounds(n - 1)(acc + sum(ints(3)))

let total n =
    Ashes.Text.fromInt(n) + ":" + Ashes.Text.fromInt(rounds(n)(0))

let intsShown =
    3
    |> ints
    |> showInts

let strsShown =
    "c"
    |> strs
    |> showStrs

[intsShown, strsShown, total(1), total(1000), total(10000)]
|> Ashes.Text.join(" ")
|> Ashes.IO.print
