// expect: 3|7|8|9
let xs = [7u8, 8u8, 9u8]
in
    let b = Ashes.Byte.fromList(xs)
    in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Byte.length(b)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b)(0)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b)(1)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b)(2)))
