// Two selected defaults that call each other, with no supplied method breaking the cycle.
// expect-compile-error: ASH025 Default-method dependency cycle in implementation of 'Choice': first -> second -> first.
trait Choice(a) =
    | first : a -> Bool =
        given (value) -> Choice.second(value)
    | second : a -> Bool =
        given (value) -> Choice.first(value)
    | base : a -> Bool

implement Choice(Int) =
    | base =
        given (value) -> true

Ashes.IO.print(1)
