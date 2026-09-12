// expect-compile-error: Undefined variable 'length'
// A qualified reference stitches the module in but, unlike an import, does not bring its
// exports into scope unqualified.
let n = Ashes.Collection.List.length([1, 2, 3])

Ashes.IO.print(Ashes.Text.fromInt(length([1, 2, 3])) + Ashes.Text.fromInt(n))
