// A multi-constructor variant threaded through a TCO loop stays in the arena and is carried
// across the back edge by the fixed-watermark compaction: once the arena has grown past twice
// the recorded live size, the successor is cloned above the cursor through the type's
// synthesized copier, the arena is reset to the loop-entry watermark, and the clone is copied
// down onto it. Three million iterations stay at a flat plateau.
// expect: 500499
type Slot =
    | Empty
    | Filled(Int, Int)

let recursive step (n: Int) (s: Slot) =
    if n == 0
    then
        match s with
            | Empty -> 0
            | Filled(a, b) -> a + b
    else
        match s with
            | Empty -> step(n - 1)(Filled(n)(1))
            | Filled(a, b) ->
                step(n - 1)(if n % 1000 == 0
                then Empty
                else Filled(a + 1)(b + n))

Ashes.IO.print(Ashes.Text.fromInt(step(3000000)(Empty)))
