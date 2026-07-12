// expect: 5
capability Ord(a) =
    | compare : a -> a -> Int

provide Ord(Int) =
    | compare =
        given (a) ->
            given (b) -> a - b

Ashes.IO.print(Ord.compare(9)(4))
