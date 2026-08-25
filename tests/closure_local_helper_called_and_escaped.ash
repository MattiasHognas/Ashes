// expect: 26
import Ashes.Collection.List.map
let recursive sumAll =
    given (xs: List(Int)) ->
        match xs with
            | [] -> 0
            | x :: rest -> x + sumAll(rest)

let total =
    given (a: Int) ->
        given (b: Int) ->
            let scale =
                given (x: Int) -> x * a + b
            in
                let direct = scale(1)
                in
                    let mapped = sumAll(map(scale)([1, 2, 3]))
                    in direct + mapped

Ashes.IO.print(total(2)(3))
