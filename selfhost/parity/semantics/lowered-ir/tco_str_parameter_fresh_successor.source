// A string loop parameter rebuilt from a fresh producer every iteration, and read again in a
// sibling argument, lives on the reference-counted heap by its type rather than through the
// affine in-place append: the successor is placed there directly, the back edge releases the
// parameter's old value, and the exit hands the last value to the caller. Three hundred
// thousand iterations stay at a flat plateau.
// expect: 3
let recursive collect (n: Int) (text: Str) (count: Int) =
    if n == 0
    then text
    else collect(n - 1)(Ashes.Text.fromInt(n) + "-x")(count + Ashes.Text.byteLength(text))

let final = collect(300000)("seed")(0)

Ashes.IO.print(Ashes.Text.fromInt(Ashes.Text.byteLength(final)))
