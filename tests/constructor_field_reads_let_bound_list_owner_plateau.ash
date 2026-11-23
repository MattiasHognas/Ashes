// expect: 100000
// A list bound by a `let` to a call's reference-counted result is stored into a constructor that is
// itself the field of a reference-counted constructor. The inner cell takes one reference to the
// list through the owned-child retain; it does not also take a reference or copy as though the read
// were of an unowned value, which left a reference nothing released on every step.
type State =
    | S(List(Int))

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

let advance r state =
    match state with
        | S(perm) ->
            let perm2 = setAt(r)(r)(perm)
            in Continue(S(perm2))(r)

let recursive loop remaining state total =
    if remaining <= 0
    then total
    else
        match advance(1)(state) with
            | Done -> total
            | Continue(next, _r) -> loop(remaining - 1)(next)(total + 1)

0
|> loop(100000)(S([1, 2, 3]))
|> Ashes.Text.fromInt
|> Ashes.IO.print
