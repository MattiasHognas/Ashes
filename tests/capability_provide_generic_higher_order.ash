// A non-recursive generic wrapper is monomorphized at its concrete call site even when the
// capability operation occurs inside a closure passed to a higher-order helper.
// expect: 1
import Ashes.Collection.List
capability Ord(a) =
    | compare : a -> a -> Int

provide Ord(Int) =
    | compare =
        given (a) ->
            given (b) -> a - b

let minOf =
    given (first) ->
        given (items) ->
            Ashes.Collection.List.foldLeft(given (best) ->
                given (next) ->
                    if Ord.compare(next)(best) < 0
                    then next
                    else best)(first)(items)

Ashes.IO.print(minOf(100)([5, 3, 9, 1, 7]))
