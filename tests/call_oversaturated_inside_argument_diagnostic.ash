// An oversaturated call chain that is itself the argument of another call counts only its own
// arguments, not those of the call around it.
// expect-compile-error: Call to 'add' expects 2 argument(s) but got 3.
let add (a: Int) (b: Int) = a + b

let twice f x =
    x
    |> f
    |> f

4
|> twice(add(1)(2)(3))
|> Ashes.IO.print
