// expect: 255|0|200|55|1
// u8 arithmetic: basic values and subtraction
let a = 255u8
in 
    let b = 0u8
    in 
        let c = 200u8
        in 
            let d = 55u8
            in Ashes.IO.print(Ashes.Text.fromInt(a) + "|" + Ashes.Text.fromInt(b) + "|" + Ashes.Text.fromInt(c) + "|" + Ashes.Text.fromInt(d) + "|" + Ashes.Text.fromInt(c - d - 144u8))
