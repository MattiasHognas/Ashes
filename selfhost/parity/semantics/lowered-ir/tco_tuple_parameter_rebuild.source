// A tuple of scalars rebuilt on every iteration of a tail-recursive loop lives on the
// reference-counted heap as a loop parameter, exactly like a single-constructor record: the
// caller's cell is normalized at entry, each back edge copies the fresh cell out before the
// iteration's arena allocations are reclaimed and releases the predecessor, and the exit
// releases the last value. Three million iterations stay at a flat plateau.
// expect: 4500001500003
let recursive step (n: Int) (s: (Int, Int)) =
    match s with
        | (a, b) ->
            if n == 0
            then a + b
            else step(n - 1)((b, a + n))

Ashes.IO.print(Ashes.Text.fromInt(step(3000000)((1, 2))))
