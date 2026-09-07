// A string field read out of a runtime-managed record loop parameter and stored into the
// parameter's own successor is a borrow of a child the parameter owns: the back edge copies the
// successor's string and releases the dying successor's reference to the original, then the old
// parameter's structural release frees the original again unless the stored read was retained.
// The seed is built at runtime so no immortal literal header hides the double release.
// expect: 12345|20000100000
type State =
    | label: Str
    | count: Int

let recursive step (n: Int) (s: State) =
    if n == 0
    then s
    else step(n - 1)(State(label = s.label, count = s.count + n))

let final = step(200000)(State(label = Ashes.Text.fromInt(12345), count = 0))

Ashes.IO.print(final.label + "|" + Ashes.Text.fromInt(final.count))
