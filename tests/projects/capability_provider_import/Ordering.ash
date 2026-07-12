capability Ord(a) =
    | compare : a -> a -> Int

provide Ord(Int) =
    | compare =
        given (x) ->
            given (y) -> x - y

let recursive minOf : a -> List(a) -> a needs {Ord(a)} =
    given (best) ->
        given (items) ->
            match items with
                | [] -> best
                | x :: rest ->
                    if Ord.compare(x)(best) < 0
                    then minOf(x)(rest)
                    else minOf(best)(rest)
