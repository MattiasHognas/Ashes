// expect: 0|1114111|none
let show value =
    match value with
        | None -> "none"
        | Some(rune) -> Ashes.Text.fromInt(Ashes.Rune.toInt(rune))
in Ashes.IO.print(show(Ashes.Rune.fromInt(0)) + "|" + show(Ashes.Rune.fromInt(1114111)) + "|" + show(Ashes.Rune.fromInt(-1)))
