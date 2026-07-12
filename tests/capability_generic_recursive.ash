// expect: 1
capability Ord(a) =
    | compare : a -> a -> Int

provide Ord(Int) =
    | compare =
        given (a) ->
            given (b) -> a - b

let recursive minOf : a -> List(a) -> a needs {Ord(a)} =
    given (best) ->
        given (items) ->
            match items with
                | [] -> best
                | x :: rest ->
                    if Ord.compare(x)(best) < 0
                    then minOf(x)(rest)
                    else minOf(best)(rest)

Ashes.IO.print(minOf(100)([5, 3, 9, 1, 7]))
