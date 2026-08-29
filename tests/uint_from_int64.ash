// expect: -1|0|1|-9223372036854775808|true
// Ashes.Number.UInt.fromInt64 bit-reinterprets a signed Int as u64 with no masking (unlike
// Ashes.Number.UInt.fromInt's u8 narrowing), since Int and u64 are both full-width i64 words.
// Covers: round-tripping several values (including i64::MIN) through fromInt64 -> toInt back to
// the original Int, and a genuine unsigned comparison -- if fromInt64 still behaved as signed,
// -1's bit pattern would compare LESS than 1000000, not greater, as an unsigned u64.
import Ashes.IO
import Ashes.Text
import Ashes.Number.UInt
let roundTrip x = Ashes.Number.UInt.toInt(Ashes.Number.UInt.fromInt64(x))

let negativeOne = roundTrip(-1)

let zero = roundTrip(0)

let one = roundTrip(1)

let minInt = roundTrip(-9223372036854775807 - 1)

let unsignedOrderFlips = Ashes.Number.UInt.fromInt64(-1) > Ashes.Number.UInt.fromInt64(1000000)
in
    Ashes.IO.print(
        Ashes.Text.fromInt(negativeOne) + "|" + Ashes.Text.fromInt(zero) + "|" + Ashes.Text.fromInt(one) + "|" + Ashes.Text.fromInt(minInt) + "|" + (if unsignedOrderFlips
        then "true"
        else "false")
    )
