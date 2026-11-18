// A written requirement on known types is supplied by its implementation, not by the binding's
// users, so a `requires` clause stating it is unjustified.
// expect-compile-error: Written requires clause includes unjustified requirement 'Ashes.Trait.Eq(Int)'
let same : Int -> Bool requires {Eq(Int)} =
    given (x) -> x == x

3
|> same
|> Ashes.IO.print
