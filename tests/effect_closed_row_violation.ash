// expect-compile-error: not permitted by the closed row

effect Prices =
    | lookup : Str -> Int

effect Clock =
    | now : Unit -> Int

let priceOf : Str -> Int uses {Prices} = 
    given (item) -> Prices.lookup(item)

let bad : Str -> Int uses {Prices} = 
    given (item) -> priceOf(item) + Clock.now(Unit)

Ashes.IO.print("unreachable")
