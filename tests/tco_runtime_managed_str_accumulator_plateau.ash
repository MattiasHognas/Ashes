// expect: 200000|1088895
// A runtime-managed TCO loop parameter plateau: a `Str` accumulator rebuilt through `+` on every
// back edge, and a `List(Str)` consumed via its own pattern-owned tail, each run for 200000
// iterations so a per-iteration leak or double release of the accumulator's own reference shows
// up as a crash or a wrong summary rather than only a memory-usage regression too small to
// notice at a handful of iterations.
let recursive accumulateStr n acc =
    if n == 0
    then acc
    else accumulateStr(n - 1)(acc + "x")

let recursive buildTexts n acc =
    if n == 0
    then acc
    else buildTexts(n - 1)(Ashes.Text.fromInt(n) :: acc)

let recursive sumTextLengths n xs total =
    match xs with
        | [] -> total
        | s :: rest -> sumTextLengths(n - 1)(rest)(total + Ashes.Text.byteLength(s))

Ashes.IO.print(
    Ashes.Text.fromInt(Ashes.Text.byteLength(accumulateStr(200000)(""))) + "|" + Ashes.Text.fromInt(sumTextLengths(200000)(buildTexts(200000)([]))(0))
)
