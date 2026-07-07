// expect: ok
// assertEqual works across the basic types (Str, Int, Float, Bool) within one program: an
// overload-generic function (it compares two of its parameters) is inlined and type-resolved at
// each concrete call site. A user-defined function with the same shape is polymorphic the same way.
import Ashes.Test
let sameShape a b = 
    if a == b
    then "eq"
    else "ne"

let s = assertEqual("hi")("h" + "i")

let i = assertEqual(3)(1 + 2)

let f = assertEqual(1.5)(0.5 + 1.0)

let b = assertEqual(true)(1 == 1)

let bf = assertEqual(false)(1 == 2)

let u1 = assertEqual("eq")(sameShape(7)(7))

let u2 = assertEqual("ne")(sameShape("a")("b"))

let u3 = assertEqual("eq")(sameShape(true)(true))

Ashes.IO.print("ok")
