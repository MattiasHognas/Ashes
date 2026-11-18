// A qualified name whose qualifier names no module, and no binding a field could be read from,
// reports the qualifier as an unknown module.
// expect-compile-error: Unknown module 'Foo'.
let x = Foo.bar

x
