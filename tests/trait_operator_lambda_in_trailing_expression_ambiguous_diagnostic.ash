// An operator over a lambda parameter in the trailing expression, which no binding generalizes and
// nothing applies, has no type to dispatch on: it is ambiguous, with no numeric default.
// expect-compile-error: Trait requirement 'Ashes.Trait.Add(a)' is ambiguous: its type cannot be determined from context
[given (x) -> x + x]
|> Ashes.Collection.List.length
|> Ashes.IO.print
