// expect: 5
capability Ord(a) =
    | compare : a -> a -> Int

provide Ord(Int) =
    | compare =
        given (a) ->
            given (b) -> a - b

4
|> Ord.compare(9)
|> Ashes.IO.print
