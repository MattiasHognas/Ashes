// Regression: a positional product passed across a TCO back edge owns its runtime-managed child
// lists. The children move into the successor ADT instead of being dropped with their let scopes.
// expect: 18
import Ashes.IO as io
type State =
    | S(List(Int), List(Int))

let replaceFirst value values =
    match values with
        | [] -> [value]
        | _ :: rest -> value :: rest

let recursive sum values total =
    match values with
        | [] -> total
        | value :: rest -> sum(rest)(total + value)

let recursive loop n state =
    if n == 0
    then
        match state with
            | S(left, right) -> sum(left)(sum(right)(0))
    else
        match state with
            | S(left, right) ->
                let nextLeft = replaceFirst(n)(left)
                in
                    let nextRight = replaceFirst(n + 10)(right)
                    in loop(n - 1)(S(nextLeft)(nextRight))

io.print(loop(2)(S([1, 2])([3, 4])))
