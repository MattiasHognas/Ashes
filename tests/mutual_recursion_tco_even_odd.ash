// expect: true
let recursive isEven n =
    match n with
        | 0 -> true
        | _ -> isOdd(n - 1)
and isOdd n =
    match n with
        | 0 -> false
        | _ -> isEven(n - 1)

1000000
|> isEven
|> Ashes.IO.print
