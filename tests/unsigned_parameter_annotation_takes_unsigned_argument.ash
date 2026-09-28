// Parameters annotated with the unsigned integer types take arguments of those types.
// expect: 62
let f8 =
    given (p: u8) -> p

let f16 =
    given (p: u16) -> p

let f32 =
    given (p: u32) -> p

let f64 =
    given (p: u64) -> p

Ashes.IO.print(Ashes.Number.UInt.toInt(f8(14u8)) + Ashes.Number.UInt.toInt(f16(15u16)) + Ashes.Number.UInt.toInt(f32(16u32)) + Ashes.Number.UInt.toInt(f64(17u64)))
