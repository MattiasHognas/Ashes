// expect-compile-error: Unknown constructor 'Foo'.
type Maybe =
    | None
    | Some(T)

Foo(1)
