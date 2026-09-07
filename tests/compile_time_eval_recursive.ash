// expect: 832040
let recursive fib =
    given (n) ->
        if n < 2
        then n
        else fib(n - 1) + fib(n - 2)

30
|> fib
|> Ashes.IO.print
