// expect: true
import Ashes.Trait
let same : a -> a -> Bool requires {Eq(a)} =
    given (left) ->
        given (right) -> left == right

let shared = [1, 2, 3]

shared
|> same(shared)
|> Show.show
|> Ashes.IO.print
