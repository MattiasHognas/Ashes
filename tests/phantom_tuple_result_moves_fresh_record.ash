// A loop exits with a tuple of a fresh record, an integer and `None`: the tuple is built in the
// arena and copied onto the heap at the return, and the fresh record moves into the copy.
// expect: 32
type State =
    | counter: Int
    | items: List(Int)

let recursive build (n: Int) (acc: Int) =
    if n == 0
    then (State(counter = acc, items = [acc, acc + 1]), acc + 1, None)
    else build(n - 1)(acc + n)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match build(3)(n) with
            | (state, m, _h) -> rounds(n - 1)(total + state.counter + m)

0
|> rounds(2)
|> Ashes.IO.print
