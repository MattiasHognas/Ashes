// A provider that supplies one operation twice is rejected.
// expect-compile-error: ASH026 Provider for 'Clock' supplies operation 'now' more than once.
capability Clock =
    | now : Unit -> Int

provide Clock =
    | now =
        given (u) -> 1
    | now =
        given (u) -> 2

Ashes.IO.print(perform Clock.now(Unit))
