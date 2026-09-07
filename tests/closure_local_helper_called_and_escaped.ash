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
                    let mapped =
                        [1, 2, 3]
                        |> map(scale)
                        |> sumAll
                    in direct + mapped

3
|> total(2)
|> Ashes.IO.print
