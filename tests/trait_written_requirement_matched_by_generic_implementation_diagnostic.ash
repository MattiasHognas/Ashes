// A written requirement an implementation head matches is reduced by that implementation: the
// binding states what the implementation leaves, `Eq(a)`, not `Eq(List(a))`.
// expect-compile-error: Written requires clause includes unjustified requirement 'Ashes.Trait.Eq(List
let listed : a -> Bool requires {Eq(List(a))} =
    given (x) -> [x] == [x]

2
|> listed
|> Ashes.IO.print
