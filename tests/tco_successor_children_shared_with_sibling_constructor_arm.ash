// expect: 200000 37
// Two let-bound runtime-managed lists are moved into the tail self-call's successor on one arm
// and retained into a returned constructor on the sibling arm. The move belongs to the tail-call
// path only: the sibling arm's path still releases the bindings at the let scope's exit, so the
// retained references stay balanced instead of leaking both lists on every returned step.
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

let recursive advance r n state =
    if r == n
    then Done
    else
        match state with
            | S(perm, count) ->
                let perm2 = setAt(r)(r)(perm)
                in
                    let count2 = setAt(r)(r)(count)
                    in
                        if r % 2 == 0
                        then Continue(S(perm2)(count2))(r)
                        else advance(r + 1)(n)(S(perm2)(count2))

let finish state total =
    match state with
        | S(perm, count) -> Ashes.Text.fromInt(total) + " " + Ashes.Text.fromInt(sumList(perm) + sumList(count))

let recursive loop remaining state total =
    if remaining <= 0
    then finish(state)(total)
    else
        match advance(1)(8)(state) with
            | Done -> finish(state)(total)
            | Continue(next, r) -> loop(remaining - 1)(next)(total + 1)

Ashes.IO.print(loop(200000)(S([1, 2, 3, 4, 5, 6, 7, 8])([0, 0, 0, 0, 0, 0, 0, 0]))(0))
