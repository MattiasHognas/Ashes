// Ashes.Console on a non-tty stdin (the test harness pipes stdin): enableRawInput reports false
// and changes nothing, pollInput drains the piped bytes and returns None at end of input, the
// monotonic clock never goes backwards, and restoreInput is a safe no-op.
// stdin: ping
// expect: raw no|got ping|clock ok
import Ashes.Console as console
import Ashes.IO as io
let recursive drain acc =
    match console.pollInput(50) with
        | None -> acc
        | Some(chunk) -> drain(acc + chunk)

let rawLabel =
    if console.enableRawInput(Unit)
    then "raw yes"
    else "raw no"

let startMillis = console.monotonicMillis(Unit)

let collected = drain("")

let _restored = console.restoreInput(Unit)

let clockLabel =
    if console.monotonicMillis(Unit) >= startMillis
    then "clock ok"
    else "clock bad"

io.write(rawLabel + "|got " + collected + "|" + clockLabel + "\n")
