// A capability operation declared without a signature cannot be used as a value.
// expect-compile-error: ASH019 Capability operation 'Clock.now' needs an explicit signature to be used as a value.
capability Clock =
    | now

let read = Clock.now

Ashes.IO.print(1)
