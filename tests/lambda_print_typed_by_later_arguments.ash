// A lambda passed to a function is lowered before the arguments that follow it; a literal among
// them types the lambda's parameter first, so `print` inside it knows what it prints.
// expect: 3
let apply g v = g(v)

apply(given (x) -> Ashes.IO.print(x))(3)
