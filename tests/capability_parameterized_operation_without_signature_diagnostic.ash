// An operation of a parameterized capability must write its signature.
// expect-compile-error: ASH019 Operation 'get' of parameterized capability 'Store' requires an explicit signature.
capability Store(a) =
    | get

Ashes.IO.print(1)
