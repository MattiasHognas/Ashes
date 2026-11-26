// expect: 20000
// A loop's `let` binds cons cells over the lists matched out of its state, and every read of them
// copies them onto the reference-counted heap: the returned constructor on one arm, the
// successor state on the other. The copies share the matched lists with references of their
// own, so the duplicates the arena cells took of their tails are withdrawn rather than left
// unreleased on every step.
type State =
    | S(List(Int), List(Int))

type Step =
    | Done
    | Continue(State, Int)

let recursive advance r n state =
    if r == n
    then Done
    else
        match state with
            | S(perm, count) ->
                let perm2 = 0 :: perm
                in
                    let count2 = 0 :: count
                    in
                        if r % 2 == 0
                        then
                            Continue(S(perm2)(count2))(r)
                        else
                            count2
                            |> S(perm2)
                            |> advance(r + 1)(n)

let recursive loop remaining state total =
    if remaining <= 0
    then total
    else
        match advance(1)(8)(state) with
            | Done -> total
            | Continue(next, r) -> loop(remaining - 1)(next)(total + 1)

0
|> loop(20000)(S([1, 2])([0, 0]))
|> Ashes.Text.fromInt
|> Ashes.IO.print
