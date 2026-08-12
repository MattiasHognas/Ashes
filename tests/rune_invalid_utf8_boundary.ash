// expect: 65533|x
let malformed = Ashes.Byte.subText(Ashes.Byte.fromList([255u8, 120u8]))(0)(2)
in
    match Ashes.Text.uncons(malformed) with
        | None -> Ashes.IO.print("empty")
        | Some((rune, tail)) -> Ashes.IO.print(Ashes.Text.fromInt(Ashes.Rune.toInt(rune)) + "|" + tail)
