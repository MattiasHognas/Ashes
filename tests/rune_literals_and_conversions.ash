// expect: A|233|€|😀|0|1114111|none|none
let showMaybe value =
    match value with
        | None -> "none"
        | Some(rune) -> Ashes.Rune.toText(rune)
in Ashes.IO.print(Ashes.Rune.toText('A') + "|" + Ashes.Text.fromInt(Ashes.Rune.toInt('é')) + "|" + Ashes.Rune.toText('€') + "|" + Ashes.Rune.toText('😀') + "|" + Ashes.Text.fromInt(Ashes.Rune.toInt('\0')) + "|" + Ashes.Text.fromInt(Ashes.Rune.toInt('􏿿')) + "|" + showMaybe(Ashes.Rune.fromInt(55296)) + "|" + showMaybe(Ashes.Rune.fromInt(1114112)))
