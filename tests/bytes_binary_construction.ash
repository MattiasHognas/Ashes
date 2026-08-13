// expect: 16|0|170|4660|2309737967|72623859790382856|1|1|2|3|4|5

let original = Ashes.Byte.allocate(16)

let patched = Ashes.Byte.setU64Le(Ashes.Byte.setU32Le(Ashes.Byte.setU16Le(Ashes.Byte.set(original)(0)(170u8))(1)(4660u16))(3)(2309737967u32))(7)(72623859790382856u64)

let overlapping = Ashes.Byte.fromList([1u8, 2u8, 3u8, 4u8, 5u8])

let shifted = Ashes.Byte.copyRange(overlapping)(1)(overlapping)(0)(4)

let unchanged = Ashes.Byte.copyRange(shifted)(5)(shifted)(5)(0)

let show value = Ashes.Text.fromInt(value)

let byte bytes index = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(index))

Ashes.IO.print(show(Ashes.Byte.length(patched)) + "|" + show(byte(original)(0)) + "|" + show(byte(patched)(0)) + "|" + show(Ashes.Number.UInt.toInt(Ashes.Byte.getU16Le(patched)(1))) + "|" + show(Ashes.Number.UInt.toInt(Ashes.Byte.getU32Le(patched)(3))) + "|" + show(Ashes.Number.UInt.toInt(Ashes.Byte.getU64Le(patched)(7))) + "|" + show(byte(unchanged)(0)) + "|" + show(byte(unchanged)(1)) + "|" + show(byte(unchanged)(2)) + "|" + show(byte(unchanged)(3)) + "|" + show(byte(unchanged)(4)) + "|" + show(Ashes.Byte.length(unchanged)))
