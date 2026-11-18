// A binding's requirement that still names a type variable is passed on to the binding's users
// with its scheme, even when no implementation in scope could supply it: it is resolved where a
// use settles the variable, and these bindings have none.
// expect: ok
trait Named(a) =
    | label : a -> Str

let describe value = Named.label(value)

let describeAll : List(a) -> Str requires {Named(List(a))} =
    given (values) -> Named.label(values)

let same (f: a -> a) = f == f

Ashes.IO.print("ok")
