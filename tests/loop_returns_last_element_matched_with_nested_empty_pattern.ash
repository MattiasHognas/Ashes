// expect: 70 7
let recursive lastPiece (pieces: List(Str)) =
    match pieces with
        | [] -> ""
        | last :: [] -> last
        | _ :: rest -> lastPiece(rest)

let labelDigits (name: Str) =
    match "_"
    |> Ashes.Text.split(name)
    |> lastPiece with
        | "" -> 0
        | digits -> Ashes.Text.byteLength(digits)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        loop(n - 1)(acc + Ashes.Text.byteLength(lastPiece(["rc", "drop", Ashes.Text.fromInt(1000000 + n)])))

Ashes.IO.print(Ashes.Text.fromInt(loop(10)(0)) + " " + Ashes.Text.fromInt(labelDigits("rc_drop_1000153")))
