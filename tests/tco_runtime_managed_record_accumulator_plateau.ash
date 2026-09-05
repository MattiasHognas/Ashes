// A single-constructor record rebuilt on every iteration of a tail-recursive loop lives on the
// reference-counted heap as a loop parameter: the caller's cell is normalized at entry, each back
// edge copies the fresh cell out before the iteration's arena allocations are reclaimed and
// releases the predecessor, and the exit releases or transfers the last value. Three million
// iterations keep the process at a flat plateau instead of growing the arena by one cell each.
// expect: 4500004500000
type Counter =
    | count: Int
    | total: Int

let recursive bump (n: Int) (c: Counter) =
    if n == 0
    then c
    else bump(n - 1)(Counter(count = c.count + 1, total = c.total + n))

let bumped = bump(3000000)(Counter(count = 0, total = 0))

Ashes.IO.print(Ashes.Text.fromInt(bumped.count + bumped.total))
