// A call chain rooted at a lambda that passes more arguments than the lambda takes names no callee.
// expect-compile-error: Call expects 1 argument(s) but got 2.
2
|> ((given (x) -> x + 1))(1)
|> Ashes.IO.print
