// Regression: record field access on a receiver captured from an enclosing scope. A multi-param
// let desugars to nested lambdas, so `p.field` in the body reads `p` through the closure
// environment. Previously the free-var walk did not treat the receiver of a field access as a
// capturable use ("Unknown module 'p'"), and the field-access fallback had no Binding.Env arm
// ("Module 'p' does not export 'x'").
// expect: 20
type Point =
    | x: Int
    | y: Int

let origin = Point(x = 1, y = 2)

let bump (p: Point) times = p.x + times

let scaled (p: Point) factor = p with x = p.x * factor

let grown = scaled(origin)(5)

let viaCapture =
    (let q = origin
    in
        ((given (t) -> q.x + t))(10))

Ashes.IO.print(bump(origin)(3) + grown.x + viaCapture)
