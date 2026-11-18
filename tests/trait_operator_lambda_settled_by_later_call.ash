// An operator over a lambda parameter is settled by the call that later applies the lambda.
// expect: 4
let applyToTwo =
    given (f) -> f(2)

(given (x) -> x + x)
|> applyToTwo
|> Ashes.IO.print
