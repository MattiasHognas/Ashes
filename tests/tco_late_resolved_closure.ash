// Regression: the closure accumulator is unannotated and becomes a concrete function type only
// after the TCO body has been inferred. Post-body placement must install its one-time active-slot
// prologue before the loop, then balance runtime closure ownership at back edges and exit.
// expect: xxxx1
let recursive loop n text transform =
    if n <= 0
    then transform(text)
    else
        loop(n - 1)(text + "x")(given (value) -> value + Ashes.Text.fromInt(n))

Ashes.IO.print(loop(4)("")(given (value) -> value))
