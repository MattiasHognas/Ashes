// A loop whose successor record, built at the self-call, stores a field of the loop parameter beside
// a fresh record: the cell retains that field, and the back edge's copy releases the dying cell's
// references, so a field replaced every few iterations is released each time.
// expect: 41
type State =
    | label: Str
    | count: Int

type Pair =
    | previous: State
    | current: State

let recursive walk (n: Int) (pair: Pair) =
    if n == 0
    then pair
    else
        if n % 3 == 0
        then walk(n - 1)(Pair(previous = State(label = Ashes.Text.fromInt(n), count = n), current = State(label = Ashes.Text.fromInt(n + 1), count = n)))
        else walk(n - 1)(Pair(previous = State(label = Ashes.Text.fromInt(n), count = n), current = pair.current))

let final = walk(3000)(Pair(previous = State(label = "a", count = 0), current = State(label = Ashes.Text.fromInt(7), count = 0)))

let current = final.current

let previous = final.previous

Ashes.IO.print(current.label + previous.label)
