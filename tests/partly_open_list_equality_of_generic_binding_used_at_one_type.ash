// A recursive binding compares its list parameter with the empty list before anything settles the
// element type; the program uses it at one element type only.
// expect: 3 1
let recursive countUntilEmpty xs n =
    if xs == []
    then n
    else
        match xs with
            | _ :: rest -> countUntilEmpty(rest)(n + 1)
            | [] -> n

Ashes.IO.print(Ashes.Text.fromInt(countUntilEmpty(["a", "b", "c"])(0)) + " " + Ashes.Text.fromInt(countUntilEmpty(["d"])(0)))
