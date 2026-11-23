// expect: yes no
type Key =
    | Key(Int)

implement Eq(Key) =
    | equal =
        given (left) ->
            given (right) ->
                match (left, right) with
                    | (Key(x), Key(y)) -> x % 2 == y % 2

let same : List(a) -> List(a) -> Bool requires {Eq(a)} =
    given (left) ->
        given (right) -> left == right

let word result =
    if result
    then "yes"
    else "no"

Ashes.IO.print(word(same([Key(2)])([Key(4)])) + " " + word(same([Key(2)])([Key(3)])))
