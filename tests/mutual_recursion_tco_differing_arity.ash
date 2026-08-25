// expect: 1
import Ashes.Collection.List.length
let recursive countDown =
    given (n: Int) ->
        if n <= 0
        then []
        else collect(n - 1)([n])
and collect =
    given (n: Int) ->
        given (acc: List(Int)) ->
            if n <= 0
            then acc
            else countDown(n - 1)

Ashes.IO.print(length(countDown(10000001)))
