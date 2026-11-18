// A provider for a capability no declaration names is rejected.
// expect-compile-error: ASH019 'provide' refers to unknown capability 'Missing'.
provide Missing =
    | now =
        given (u) -> 1

Ashes.IO.print(1)
