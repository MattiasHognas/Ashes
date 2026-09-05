// expect: 200000|1088895|200000
// Runtime-managed list loop parameters at plateau scale: a `List(Str)` accumulator grown by one
// fresh cons cell per iteration and returned, then consumed twice through its own pattern-bound
// tail from a borrowed global, and once more straight from a fresh result the loop adopts, each
// for 200000 iterations so a per-iteration leak or a release of a still-live cell shows up as a
// crash or a wrong count rather than a memory-usage regression too small to notice.
let recursive buildTexts n acc =
    if n == 0
    then acc
    else buildTexts(n - 1)(Ashes.Text.fromInt(n) :: acc)

let recursive sumTextLengths xs total =
    match xs with
        | [] -> total
        | s :: rest -> sumTextLengths(rest)(total + Ashes.Text.byteLength(s))

let recursive countTexts xs count =
    match xs with
        | [] -> count
        | _ :: rest -> countTexts(rest)(count + 1)

let texts = buildTexts(200000)([])

Ashes.IO.print(
    Ashes.Text.fromInt(countTexts(texts)(0)) + "|" + Ashes.Text.fromInt(sumTextLengths(texts)(0)) + "|" + Ashes.Text.fromInt(countTexts(buildTexts(200000)([]))(0))
)
