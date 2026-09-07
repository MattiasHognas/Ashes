// A runtime-managed `Str` loop parameter its successor reads more than once, run for 200000
// iterations so a per-iteration leak shows up as a memory plateau failure through either
// compiler: doubled behind a guard that reads its length and restarts from the seed (a join of
// a literal and a fresh append), doubled against a counter, cut back out of its own doubling by
// the standard library's substring (a stitched helper whose result joins literal arms with a
// fresh byte slice), and sliced by the byte builtin behind a literal-or-slice join.
// expect: 512|512|4096|4096
import Ashes.Text
let recursive bump (n: Int) (text: Str) =
    if n == 0
    then text
    else
        bump(n - 1)(if Ashes.Text.byteLength(text) >= 4096
        then "ab"
        else text + text)

let recursive double (n: Int) (k: Int) (text: Str) =
    if n == 0
    then text
    else
        if k == 0
        then double(n - 1)(11)("ab")
        else double(n - 1)(k - 1)(text + text)

let recursive widen (n: Int) (text: Str) =
    if n == 0
    then text
    else widen(n - 1)(text + text)

let recursive rotate (n: Int) (text: Str) =
    if n == 0
    then text
    else
        text
        |> Ashes.Text.byteLength
        |> Ashes.Text.substring(text + text)(1)
        |> rotate(n - 1)

let recursive slice (n: Int) (text: Str) =
    if n == 0
    then text
    else
        slice(n - 1)(if n % 1000 == 0
        then "ab"
        else
            Ashes.Byte.subText(Ashes.Byte.fromText(text + text))(0)(4096))

Ashes.IO.print(
    Ashes.Text.fromInt("ab"
    |> bump(200000)
    |> Ashes.Text.byteLength) + "|" + Ashes.Text.fromInt("ab"
    |> double(200000)(11)
    |> Ashes.Text.byteLength) + "|" + Ashes.Text.fromInt("ab"
    |> widen(11)
    |> rotate(200000)
    |> Ashes.Text.byteLength) + "|" + Ashes.Text.fromInt("ab"
    |> widen(11)
    |> slice(200000)
    |> Ashes.Text.byteLength)
)
