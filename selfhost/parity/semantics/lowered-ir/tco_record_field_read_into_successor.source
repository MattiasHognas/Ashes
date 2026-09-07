// A field read out of a runtime-managed record loop parameter stored into a successor record
// (its own successor in `walk`, a sibling's in `pair`) is a borrow of a child the parameter owns.
// The back edge copies the successor's children and releases the dying successor's references
// to them, then releases the old parameter's own children through its structural walk, so the
// stored child is retained or the second release frees it twice. Two hundred thousand
// iterations reuse enough freed cells to crash without the retain.
// expect: b|2|199999
type State =
    | label: Str
    | count: Int

type Pair =
    | previous: State
    | current: State

let recursive walk (n: Int) (pair: Pair) =
    if n == 0
    then pair
    else walk(n - 1)(Pair(previous = State(label = Ashes.Text.fromInt(n), count = n), current = pair.current))

let recursive carry (n: Int) (s: State) (pair: Pair) =
    if n == 0
    then pair
    else carry(n - 1)(State(label = Ashes.Text.fromInt(n), count = s.count + 1))(Pair(previous = s, current = pair.current))

let walked = walk(200000)(Pair(previous = State(label = "a", count = 0), current = State(label = "b", count = 0)))

let current = walked.current

let carried = carry(200000)(State(label = "seed", count = 0))(Pair(previous = State(label = "c", count = 0), current = State(label = "d", count = 0)))

let previous = carried.previous

Ashes.IO.print(current.label + "|" + previous.label + "|" + Ashes.Text.fromInt(previous.count))
