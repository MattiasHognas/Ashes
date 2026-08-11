// expect: 1|2|"2"
import Ashes.Trait
import Ashes.Collection.List
import Ashes.IO
let shown = List.map(Show.show)([1, 2, 3])

let matchesThree =
    given (x) -> Eq.equal(3)(x)

let countMatches = List.length(List.filter(matchesThree)([1, 2, 3, 3, 4]))

let apply2 =
    given (f) ->
        given (x) -> f(x)
in
    match shown with
        | first :: _ -> Ashes.IO.print(first + "|" + Show.show(countMatches) + "|" + Show.show(apply2(Show.show)(2)))
        | [] -> Ashes.IO.print("empty")
