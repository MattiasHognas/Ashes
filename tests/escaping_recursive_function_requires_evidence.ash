// expect-compile-error: Trait requirement 'Ashes.Trait.Not(a)' is ambiguous: its type cannot be determined from context
let recursive invert x = !x
in invert
