// expect: s
let recursive a =
    given (n: Int) ->
        if n <= 0
        then 0
        else b(n - 1)
and b =
    given (n: Int) ->
        if n <= 0
        then 1
        else a(n - 1)
and c =
    given (n: Int) -> "s"

let ten = a(10)

Ashes.IO.print(c(ten))
