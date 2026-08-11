// expect: label:1 label:2 label:3 |1,2|true
import Describable
import Reporting
implement Describable(Int) =
    | describe =
        given (value) -> Ashes.Text.fromInt(value)

let distinctEvidence =
    if Reporting.bothEqual(1)("text")
    then "true"
    else "false"

Ashes.IO.print(Reporting.report("label")([1, 2, 3]) + "|" + Describable.describeAll(1)(2) + "|" + distinctEvidence)
