// expect: done:x
let recursive ping =
    given (n: Int) ->
        given (acc: Str) ->
            if n <= 0
            then "done:" + acc
            else pong(n - 1)(n)
and pong =
    given (n: Int) ->
        given (k: Int) ->
            if n <= 0
            then "done"
            else ping(n - 1)("x")

Ashes.IO.print(ping(10000000)("start"))
