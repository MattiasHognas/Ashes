// Regression: `param.field` on a function parameter (whose type is an unbound type variable at its
// first use, under single-pass inference) reported the misleading "Module 'param' does not export
// 'field'". A record field access on a value binding whose type is unresolved is now resolved
// structurally: if exactly one record type in scope declares that field, the receiver is unified with
// a fresh instance of it. (Ambiguous / unknown still needs a type annotation, with a clear message.)
// n-body needed positional `match Body(...)` before this.
// expect: 1.5 6.0
type Body =
    | x: Float
    | y: Float
    | z: Float

let getx b = b.x

let sum b = b.x + b.y + b.z

Ashes.IO.print(Ashes.Text.formatFloat(getx(Body(x = 1.5, y = 2.5, z = 3.5)))(1) + " " + Ashes.Text.formatFloat(sum(Body(x = 1.0, y = 2.0, z = 3.0)))(1))
