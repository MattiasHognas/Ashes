// Regression: an empty list literal merged at an if/match join is reference-counted with an element
// type inference never resolved, and the scope-exit drop walks that element type. The drop emitter
// used to reject the unresolved variable outright ("Unsupported runtime-managed aggregate child"),
// failing the whole compile; such a list can hold nothing, so only its spine needs releasing.
// expect: ok 0
let fromIf =
    if false
    then []
    else []

let fromMatch =
    match 1 with
        | 0 -> []
        | _ -> []

let nested = (fromIf, fromMatch)

let sizeOf =
    given (pair) ->
        match pair with
            | (left, right) ->
                match left with
                    | [] ->
                        match right with
                            | [] -> 0
                            | _ :: _ -> 1
                    | _ :: _ -> 2

Ashes.IO.print("ok " + Ashes.Text.fromInt(sizeOf(nested)))
