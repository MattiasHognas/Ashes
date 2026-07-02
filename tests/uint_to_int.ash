// expect: 65|131|-123|16961
// Ashes.UInt.toInt widens an unsigned integer (u8/u16/u32/u64) to a signed Int, so a byte read with
// Ashes.Bytes.get can be used in Int arithmetic directly -- enabling byte-level integer parsing without
// going through strings. Covers: a single byte, a summed byte fold, a scaled-integer parse from bytes,
// and a wider getU16Le result.
import Ashes.IO
import Ashes.Text
import Ashes.Bytes
import Ashes.UInt
let rec sumBytes bytes i stop acc = 
    if i >= stop
    then acc
    else sumBytes(bytes)(i + 1)(stop)(acc + Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(i)))

let rec parseTenths bytes i stop sign acc = 
    if i >= stop
    then sign * acc
    else 
        let b = Ashes.UInt.toInt(Ashes.Bytes.get(bytes)(i))
        in 
            if b == 45
            then parseTenths(bytes)(i + 1)(stop)(-1)(acc)
            else 
                if b == 46
                then parseTenths(bytes)(i + 1)(stop)(sign)(acc)
                else parseTenths(bytes)(i + 1)(stop)(sign)(acc * 10 + b - 48)

let one = Ashes.UInt.toInt(Ashes.Bytes.get(Ashes.Bytes.fromText("A"))(0))

let ab = Ashes.Bytes.fromText("AB")

let summed = sumBytes(ab)(0)(Ashes.Bytes.length(ab))(0)

let temp = Ashes.Bytes.fromText("-12.3")

let parsed = parseTenths(temp)(0)(Ashes.Bytes.length(temp))(1)(0)

let wide = Ashes.UInt.toInt(Ashes.Bytes.getU16Le(ab)(0))
in Ashes.IO.print(Ashes.Text.fromInt(one) + "|" + Ashes.Text.fromInt(summed) + "|" + Ashes.Text.fromInt(parsed) + "|" + Ashes.Text.fromInt(wide))
