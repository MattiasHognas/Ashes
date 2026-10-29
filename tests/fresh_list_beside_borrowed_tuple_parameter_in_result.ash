// expect: 15
let recursive count (acc: List(Int)) (state: (List(Int), Str)) =
    match state with
        | ([], _source) -> (Ashes.Collection.List.reverse(acc), state)
        | (x :: rest, source) -> step(acc)((rest, source))(Some(x))
and step acc state (x: Maybe(Int)) =
    match x with
        | Some(value) -> count(value :: acc)(state)
        | None -> count(acc)(state)

let finish (acc: List(Int)) (state: (List(Int), Str)) = (Ashes.Collection.List.reverse(acc), state)

let run (seed: Int) =
    match count([])(([1, 2, 3, 4], "w" + Ashes.Text.fromInt(seed))) with
        | (found, _) ->
            match finish(found)(([seed], "v")) with
                | (again, _) -> Ashes.Collection.List.length(again) + Ashes.Collection.List.length(found)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(n))

Ashes.IO.print(rounds(2)(0) - 1)
