// expect: 4|1|2|3|4
let a = Ashes.Bytes.singleton(1u8)
in
    let b = Ashes.Bytes.singleton(2u8)
    in
        let c = Ashes.Bytes.singleton(3u8)
        in
            let d = Ashes.Bytes.singleton(4u8)
            in
                let ab = Ashes.Bytes.append(a)(b)
                in
                    let cd = Ashes.Bytes.append(c)(d)
                    in
                        let abcd = Ashes.Bytes.append(ab)(cd)
                        in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Bytes.length(abcd)) + "|" + Ashes.Text.fromInt(Ashes.Bytes.get(abcd)(0)) + "|" + Ashes.Text.fromInt(Ashes.Bytes.get(abcd)(1)) + "|" + Ashes.Text.fromInt(Ashes.Bytes.get(abcd)(2)) + "|" + Ashes.Text.fromInt(Ashes.Bytes.get(abcd)(3)))
