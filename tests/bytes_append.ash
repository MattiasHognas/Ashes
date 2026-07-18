// expect: 4|1|2|3|4
let a = Ashes.Byte.singleton(1u8)
in
    let b = Ashes.Byte.singleton(2u8)
    in
        let c = Ashes.Byte.singleton(3u8)
        in
            let d = Ashes.Byte.singleton(4u8)
            in
                let ab = Ashes.Byte.append(a)(b)
                in
                    let cd = Ashes.Byte.append(c)(d)
                    in
                        let abcd = Ashes.Byte.append(ab)(cd)
                        in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Byte.length(abcd)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(abcd)(0)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(abcd)(1)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(abcd)(2)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(abcd)(3)))
