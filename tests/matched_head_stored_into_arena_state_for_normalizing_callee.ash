// expect: 200000 41
// A list matched out of the runtime-managed loop state is stored into an arena-allocated state
// cell handed to a callee that normalizes its argument on entry. The arena cell releases nothing,
// so the store borrows the list and keeps its owner alive across the call instead of retaining a
// reference the cell can never give back; one permutation leaked per step before.
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

let recursive reset r count =
    if r == 1
    then count
    else reset(r - 1)(setAt(r - 1)(r)(count))

let advance r state =
    match state with
        | S(perm, count) -> Continue(S(perm)(count))(r)

let finish state total =
    match state with
        | S(perm, count) -> Ashes.Text.fromInt(total) + " " + Ashes.Text.fromInt(sumList(perm) + sumList(count))

let recursive loop remaining state total =
    match state with
        | S(perm, count) ->
            let count1 = reset(3)(count)
            in
                if remaining <= 0
                then finish(state)(total)
                else
                    match advance(remaining)(S(perm)(count1)) with
                        | Done -> finish(state)(total)
                        | Continue(next, r) -> loop(remaining - 1)(next)(total + 1)

Ashes.IO.print(loop(200000)(S([1, 2, 3, 4, 5, 6, 7, 8])([0, 0, 0, 0, 0, 0, 0, 0]))(0))
