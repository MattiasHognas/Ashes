// A binding whose operator requirement names a variable its own type does not mention cannot pass
// the requirement on to its users, and nothing inside it settles the variable.
// expect-compile-error: Trait requirement 'Ashes.Trait.Add(a)' is ambiguous because its type variable does not occur in the binding type
let count =
    Ashes.Collection.List.length([given (x) -> x + x])

Ashes.IO.print(count)
