// expect-compile-error: Call to 'add1' expects 1 argument(s) but got 2.
let add : Int -> Int -> Int =
    given (x) ->
        given (y) -> x + y
in
    let add1 = add(1)
    in Ashes.IO.print(add1(1)(2))
