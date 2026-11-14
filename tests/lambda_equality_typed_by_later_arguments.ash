// expect: curried list eq, string list eq, tuple eq, option ne, adt list eq, passed lambda eq, nested eq, int eq, str eq
type Value =
    | One
    | Two(Int)
    deriving {Eq}

let apply f a b = f(a)(b)

let nested =
    given (u) ->
        ((given (expected) ->
            given (actual) ->
                if expected == actual
                then "nested eq"
                else "nested ne"))([1])([u])

let curried =
    ((given (expected) ->
        given (actual) ->
            if expected == actual
            then "curried list eq"
            else "curried list ne"))([1, 2])([1, 2])

let strings =
    ((given (expected) ->
        given (actual) ->
            if expected != actual
            then "string list ne"
            else "string list eq"))(["a" + "b"])(["ab"])

let tuple =
    ((given (expected) ->
        given (actual) ->
            if expected == actual
            then "tuple eq"
            else "tuple ne"))((1, "a" + "b"))((1, "ab"))

let option =
    ((given (expected) ->
        given (actual) ->
            if expected == actual
            then "option eq"
            else "option ne"))(Some(1))(Some(2))

let custom =
    ((given (expected) ->
        given (actual) ->
            if expected == actual
            then "adt list eq"
            else "adt list ne"))([One, Two(2)])([One, Two(2)])

let passed =
    apply(given (expected) ->
        given (actual) ->
            if expected == actual
            then "passed lambda eq"
            else "passed lambda ne")([1])([1])

let int =
    ((given (expected) ->
        given (actual) ->
            if expected == actual
            then "int eq"
            else "int ne"))(3)(3)

let str =
    ((given (expected) ->
        given (actual) ->
            if expected == actual
            then "str eq"
            else "str ne"))("a" + "b")("ab")

[curried, strings, tuple, option, custom, passed, nested(1), int, str]
|> Ashes.Text.join(", ")
|> Ashes.IO.print
