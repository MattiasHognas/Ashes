// A lambda passed to a function is lowered before the arguments that follow it; a variable of a
// monomorphic type among them types the lambda's parameter first, so `print` inside it knows what
// it prints.
// expect: ab
let apply g v = g(v)

let label = "a" + "b"

apply(given (x) -> Ashes.IO.print(x))(label)
