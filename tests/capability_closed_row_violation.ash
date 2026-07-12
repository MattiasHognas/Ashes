// expect-compile-error: not permitted by the closed row

capability Prices =
    | lookup : Str -> Int

capability Clock =
    | now : Unit -> Int

let priceOf : Str -> Int needs {Prices} =
    given (item) -> Prices.lookup(item)

let bad : Str -> Int needs {Prices} =
    given (item) -> priceOf(item) + Clock.now(Unit)

Ashes.IO.print("unreachable")
