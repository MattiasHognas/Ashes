// expect: label:1 label:2 label:3 |1,2
import Describable
import Reporting
implement Describable(Int) =
    | describe =
        given (value) -> Ashes.Text.fromInt(value)

Ashes.IO.print(Reporting.report("label")([1, 2, 3]) + "|" + Describable.describeAll(1)(2))
