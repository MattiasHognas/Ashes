// expect: true false
let same =
    given (x) -> x == x

let ignore : (a -> Bool) -> a -> Bool =
    given (unused) ->
        given (x) -> true

let refuse : (a -> Bool) -> a -> Bool =
    given (unused) ->
        given (x) -> false

Ashes.IO.print(Ashes.Trait.Show.show(ignore(same)(1)) + " " + Ashes.Trait.Show.show(refuse(same)("text")))
