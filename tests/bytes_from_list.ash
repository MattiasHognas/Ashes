// expect: 3|7|8|9
let xs = [7u8, 8u8, 9u8]
in
    let b = Ashes.Bytes.fromList(xs)
    in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Bytes.length(b)) + "|" + Ashes.Text.fromInt(Ashes.Bytes.get(b)(0)) + "|" + Ashes.Text.fromInt(Ashes.Bytes.get(b)(1)) + "|" + Ashes.Text.fromInt(Ashes.Bytes.get(b)(2)))
