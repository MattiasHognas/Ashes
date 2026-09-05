// A record loop parameter consed into a sibling list accumulator lives on the reference-counted
// heap together with the accumulator: the caller's list is normalized at entry by deep-copying
// every record head into a fresh cell, the head consed at each back edge is retained for the
// cell before the back edge releases the parameter's own reference, and the exit hands the list
// to the caller. Two hundred thousand iterations reuse enough freed cells to expose a missing
// retain as a wrong total or a crash.
// expect: 1088899
type State =
    | label: Str
    | count: Int

let recursive collect (n: Int) (s: State) (acc: List(State)) =
    if n == 0
    then s :: acc
    else collect(n - 1)(State(label = Ashes.Text.fromInt(n), count = s.count + 1))(s :: acc)

let recursive labelBytes (items: List(State)) (total: Int) =
    match items with
        | [] -> total
        | item :: rest -> labelBytes(rest)(total + Ashes.Text.byteLength(item.label))

let items = collect(200000)(State(label = "seed", count = 0))([])

Ashes.IO.print(Ashes.Text.fromInt(labelBytes(items)(0)))
