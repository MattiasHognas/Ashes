// A list loop parameter rebuilt as a fresh literal at every back edge lives on the
// reference-counted heap: the caller's list is normalized at entry, each back edge copies the
// fresh arena literal out before the iteration's allocations are reclaimed and releases the
// predecessor, and the matched head the literal carries over is retained by its pattern owner.
// Three hundred thousand iterations stay at a flat plateau.
// expect: 1688898
let recursive step (n: Int) (items: List(Str)) (total: Int) =
    match items with
        | head :: _rest ->
            if n == 0
            then total
            else step(n - 1)([Ashes.Text.fromInt(n), head])(total + Ashes.Text.byteLength(head))
        | [] -> total

Ashes.IO.print(Ashes.Text.fromInt(step(300000)(["seed"])(0)))
