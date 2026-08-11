// expect: true true true
import Ashes.IO as io
let bothEqual : a -> b -> Bool requires {Eq(a), Eq(b)} =
    given (left) ->
        given (right) ->
            if left == left
            then right == right
            else false

let bothExplicit : a -> b -> Bool requires {Eq(a), Eq(b)} =
    given (left) ->
        given (right) ->
            if Eq.equal(left)(left)
            then Eq.equal(right)(right)
            else false

let forward : x -> y -> Bool requires {Eq(x), Eq(y)} =
    given (left) ->
        given (right) -> bothEqual(left)(right)

let recursive bothRecursive : a -> b -> Int -> Bool requires {Eq(a), Eq(b)} =
    given (left) ->
        given (right) ->
            given (remaining) ->
                if remaining <= 0
                then bothExplicit(left)(right)
                else bothRecursive(left)(right)(remaining - 1)

let show value =
    if value
    then "true"
    else "false"

io.print(show(forward(3)("hello")) + " " + show(bothExplicit(3)("hello")) + " " + show(bothRecursive(3)("hello")(3)))
