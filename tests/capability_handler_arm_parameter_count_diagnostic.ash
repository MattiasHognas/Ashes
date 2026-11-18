// A handler arm with more parameters than its operation takes is rejected.
// expect-compile-error: ASH020 Handler arm 'Clock.now' has 2 parameter(s) but the operation takes 1.
capability Clock =
    | now : Unit -> Int

let work =
    given (u) -> perform Clock.now(u)

let result =
    handle work(Unit) with
        | Clock.now(first, second) -> resume(1)

Ashes.IO.print(result)
