// expect-compile-error: Unknown constructor 'Foo'.
type Option =
    | None
    | Some(T)

Foo(1)
