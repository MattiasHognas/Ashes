// A state tuple whose list-of-records element keeps the tuple an arena shell threads a large string
// through a helper that rebuilds the tuple around it. The string is bound out of the borrowed
// parameter, so the rebuilt tuple carries it as is instead of cloning it per call; a let-owned fresh
// string placed into the same tuple shape is still copied, since its owner releases it at scope
// exit. Both shapes run for 20000 steps and the totals check that every string survived intact.
// expect: 131072|640000|42
import Ashes.Collection.List
type Entry =
    | key: Str
    | count: Int

let recursive widen (n: Int) (text: Str) =
    if n == 0
    then text
    else widen(n - 1)(text + text)

let step (n: Int) (state: (Str, List(Entry))) =
    match state with
        | (text, entries) -> (text, Entry(key = Ashes.Text.fromInt(n), count = n) :: entries)

let fresh (n: Int) (state: (Str, List(Entry))) =
    match state with
        | (_text, entries) ->
            let label = widen(5)(Ashes.Text.fromInt(n % 10))
            in (label, Entry(key = label, count = n) :: entries)

let recursive threaded (n: Int) (state: (Str, List(Entry))) =
    if n == 0
    then
        match state with
            | (text, entries) -> Ashes.Text.byteLength(text) * Ashes.Collection.List.length(entries) / 20000
    else threaded(n - 1)(step(n)(state))

let recursive relabeled (n: Int) (state: (Str, List(Entry))) =
    if n == 0
    then
        match state with
            | (text, entries) -> Ashes.Text.byteLength(text) * Ashes.Collection.List.length(entries)
    else relabeled(n - 1)(fresh(n)(state))

let recursive mixed (n: Int) (state: (Str, List(Entry))) =
    if n == 0
    then
        match state with
            | (text, entries) -> Ashes.Text.byteLength(text) * Ashes.Collection.List.length(entries) / 20000
    else
        mixed(n - 1)(if n % 3 == 0
        then fresh(n)(step(n)(state))
        else step(n)(state))

Ashes.IO.print(Ashes.Text.fromInt(threaded(20000)((widen(16)("ab"), []))) + "|" + Ashes.Text.fromInt(relabeled(20000)((widen(16)("ab"), []))) + "|" + Ashes.Text.fromInt(mixed(20000)((widen(16)("ab"), []))))
