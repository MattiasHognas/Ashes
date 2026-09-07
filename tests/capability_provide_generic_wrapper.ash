// expect: 5
capability Ord(a) =
    | compare : a -> a -> Int

provide Ord(Int) =
    | compare =
        given (a) ->
            given (b) -> a - b

let cmp =
    given (x) ->
        given (y) -> Ord.compare(x)(y)

4
|> cmp(9)
|> Ashes.IO.print
