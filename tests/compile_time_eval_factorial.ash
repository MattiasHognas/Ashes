// expect: 479001600
let recursive factorial =
    given (n) ->
        if n <= 1
        then 1
        else n * factorial(n - 1)

Ashes.IO.print(factorial(12))
