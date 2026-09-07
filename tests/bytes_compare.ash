// expect: -1 0 1 -1 1 0 1
import Ashes.IO
import Ashes.Byte
let r1 =
    "abd"
    |> Ashes.Byte.fromText
    |> Ashes.Byte.compare(Ashes.Byte.fromText("abc"))

let r2 =
    "abc"
    |> Ashes.Byte.fromText
    |> Ashes.Byte.compare(Ashes.Byte.fromText("abc"))

let r3 =
    "abc"
    |> Ashes.Byte.fromText
    |> Ashes.Byte.compare(Ashes.Byte.fromText("abd"))

let r4 =
    "abc"
    |> Ashes.Byte.fromText
    |> Ashes.Byte.compare(Ashes.Byte.fromText("ab"))

let r5 =
    "ab"
    |> Ashes.Byte.fromText
    |> Ashes.Byte.compare(Ashes.Byte.fromText("abc"))

let r6 =
    ""
    |> Ashes.Byte.fromText
    |> Ashes.Byte.compare(Ashes.Byte.fromText(""))

let r7 =
    "a"
    |> Ashes.Byte.fromText
    |> Ashes.Byte.compare(Ashes.Byte.fromText("b"))

Ashes.IO.writeLine(Ashes.Text.fromInt(r1) + " " + Ashes.Text.fromInt(r2) + " " + Ashes.Text.fromInt(r3) + " " + Ashes.Text.fromInt(r4) + " " + Ashes.Text.fromInt(r5) + " " + Ashes.Text.fromInt(r6) + " " + Ashes.Text.fromInt(r7))
