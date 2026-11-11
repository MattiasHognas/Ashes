// expect: passed
let appendSuffix (text: Str) (suffix: Str) = text + suffix

let makePartial (seed: Str) =
    (let source = "value" + seed
    in
        let partial = appendSuffix(source)
        in
            let alias = partial
            in
                if source == "value7"
                then alias
                else Ashes.IO.panic("source changed"))

let partial = makePartial("7")

let first = partial("-a")

let second = partial("-b")

let source = "caller" + "-owned"

let livePartial = appendSuffix(source)

let applied = livePartial("-result")

if first == "value7-a" && second == "value7-b" && source == "caller-owned" && applied == "caller-owned-result"
then Ashes.IO.print("passed")
else Ashes.IO.panic("capture released too early")
