// expect: distinct dictionary slots passed
let recursive bothEqual left right first second = left == right && first == second

let mixed = bothEqual(1)(1)("a")("a") && bothEqual("b")("b")(2)(2)

let coalesced = bothEqual(1)(1)(2)(2) && bothEqual("a")("a")("b")("b")

let unequal = bothEqual(1)(1)("a")("b") == false && bothEqual("a")("b")(2)(2) == false

if mixed && coalesced && unequal
then Ashes.IO.print("distinct dictionary slots passed")
else Ashes.IO.panic("instantiation reordered or coalesced physical dictionary slots")
