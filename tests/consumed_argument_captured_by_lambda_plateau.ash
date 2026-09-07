// expect: 206977795
// A closure capturing an entry-normalized string parameter owns the capture: the environment
// lives on the reference-counted heap with a dropper releasing the string, and the record
// storing the closure counts it as a fresh owned child, so every iteration's string is released
// exactly once. At this iteration count a leaked copy per call would grow the process past
// 150 MB; the correct program stays at its plateau.

type Box =
    | reader: Int -> Str

let box (s: Str) =
    Box(reader = given (u: Int) -> s)

let recursive fill (n: Int) (acc: Str) =
    if n == 0
    then acc
    else fill(n - 1)(acc + "abcdefgh")

let recursive loop (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let kept = box(fill(64)(Ashes.Text.fromInt(n)))
        in
            let scratch = fill(64)(Ashes.Text.fromInt(n + 1))
            in
                match kept with
                    | Box { reader = reader } -> loop(n - 1)(total + Ashes.Text.byteLength(reader(0)) + Ashes.Text.byteLength(scratch))

Ashes.IO.print(Ashes.Text.fromInt(loop(200000)(0)))
