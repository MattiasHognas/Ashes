// expect: 3600
// A fresh buffer handed straight to a builtin that only reads it (a length, an element read, a
// comparison, a copied-out text, a copyRange source) is released once the read completes.
let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let wide =
            Ashes.Byte.copyRange(Ashes.Byte.allocate(4))(0)(Ashes.Byte.fromList([7u8, 8u8]))(0)(2)
        in
            let read =
                Ashes.Byte.length(Ashes.Byte.allocate(3)) + Ashes.Number.UInt.toInt(Ashes.Byte.get(Ashes.Byte.fromList([5u8, 6u8]))(1))
            in
                let compared =
                    [1u8]
                    |> Ashes.Byte.fromList
                    |> Ashes.Byte.compare(Ashes.Byte.fromList([1u8]))
                in
                    let text =
                        Ashes.Byte.subText(Ashes.Byte.fromText("ember"))(1)(3)
                    in
                        rounds(n - 1)(total + read + compared + Ashes.Text.byteLength(text) + Ashes.Number.UInt.toInt(Ashes.Byte.get(wide)(1)) + Ashes.Byte.length(wide))

0
|> rounds(150)
|> Ashes.IO.print
