// expect: a:2|b:7|loop:10
import Ashes.Text
import Ashes.IO
// A closure parameter that returns a record, applied in a pipeline stage and threaded through a
// loop. The record is a general reference-counted value: the caller takes it owned through the
// callee's returns bit, as stage 0 does, instead of asking the unknown callee for an arena copy, and
// releases it after its last use.
type Counter =
    | name: Str
    | total: Int

type Tagged =
    | Tagged(Counter)
    | Untagged

let bump (label: Str) (by: Int) = Counter(name = label, total = by)

let tag (counter: Counter) = Tagged(counter)

let counterText (counter: Counter) = counter.name + ":" + Ashes.Text.fromInt(counter.total)

let piped =
    "a"
    |> bump
    |> (given (f) -> f(2))

let taggedText =
    match 7
    |> bump("b")
    |> (given (counter) -> tag)
    |> (given (wrap) ->
        7
        |> bump("b")
        |> wrap) with
        | Tagged(counter) -> counterText(counter)
        | Untagged -> "none"

let recursive run step n (acc: Counter) =
    if n == 0
    then acc
    else
        acc
        |> step
        |> run(step)(n - 1)

let looped =
    run(given (counter) -> Counter(name = "loop", total = counter.total + 2))(5)(Counter(name = "start", total = 0))
in Ashes.IO.print(counterText(piped) + "|" + taggedText + "|" + counterText(looped))
