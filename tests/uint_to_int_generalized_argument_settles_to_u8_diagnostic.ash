// `Ashes.Number.UInt.toInt` accepts any unsigned width, but a binding that would generalize over its
// argument settles that argument to `u8`, so a later `u16` argument is a mismatch.
// expect-compile-error: Type mismatch: u8 vs u16. Context: in argument #1 of call to 'widen'.
import Ashes.Number.UInt as uint
let widen value = uint.toInt(value)

let byte : u8 = uint.fromInt(3)

let wide : u16 = 7u16

Ashes.IO.print(widen(byte) + widen(wide))
