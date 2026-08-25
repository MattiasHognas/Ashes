// expect: 200000 7
type State =
    | S(List(Int), List(Int))

type Step =
    | Done
    | Continue(State, Int)

let recursive setAt i value values =
    match values with
        | [] -> []
        | head :: tail ->
            if i == 0
            then value :: tail
            else head :: setAt(i - 1)(value)(tail)

let recursive sumList values =
    match values with
        | [] -> 0
        | head :: tail -> head + sumList(tail)

let advance r state =
    match state with
        | S(perm, count) -> Continue(S(perm)(setAt(0)(r)(count)))(r)

let finish state total =
    match state with
        | S(perm, count) -> Ashes.Text.fromInt(total) + " " + Ashes.Text.fromInt(sumList(perm) + sumList(count))

let recursive loop remaining state total =
    if remaining <= 0
    then finish(state)(total)
    else
        match advance(remaining)(state) with
            | Done -> finish(state)(total)
            | Continue(next, r) -> loop(remaining - 1)(next)(total + 1)

Ashes.IO.print(loop(200000)(S([1, 2, 3])([0, 0, 0]))(0))
