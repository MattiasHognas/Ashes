// A record with a string field rebuilt on every iteration of a tail-recursive loop lives on the
// reference-counted heap as a loop parameter: the caller's cell and its string are copied at
// entry, each back edge copies the fresh cell and its string out before the iteration's arena
// allocations are reclaimed and releases the predecessor's children with its cell, and the exit
// transfers the last value to the caller. Three million iterations stay at a flat plateau.
// expect: 1|19888899
type State =
    | label: Str
    | count: Int

let recursive step (n: Int) (s: State) =
    if n == 0
    then s
    else step(n - 1)(State(label = Ashes.Text.fromInt(n), count = s.count + Ashes.Text.byteLength(s.label)))

let final = step(3000000)(State(label = "seed", count = 0))

Ashes.IO.print(final.label + "|" + Ashes.Text.fromInt(final.count))
