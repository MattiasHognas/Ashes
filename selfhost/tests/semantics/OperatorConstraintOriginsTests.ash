import AshesCompiler.Frontend.Parser
import AshesCompiler.Semantics.OperatorConstraintOrigins.mayOriginateOperatorConstraint
export (
    value runOperatorConstraintOriginsTests,
)

// The scope the queries run in: one constrained function, one trait method, one constrained record.
let carries name = name == "sameAs" || name == "Eq.equal" || name == "ordering"

let expectOrigin expected source =
    match parseExpression(source) with
        | ExpressionParseResult { expression = expression, diagnostics = [] } ->
            if mayOriginateOperatorConstraint(carries)(expression) == expected
            then Unit
            else Ashes.IO.panic("operator constraint origin changed for: " + source)
        | _ -> Ashes.IO.panic("operator constraint origin source did not parse: " + source)

// Arithmetic maps to traits no plan supports, and a comparison with a literal is a requirement at
// the literal's own type.
let checkNoOrigin unit =
    Unit
    |> (given (_) -> expectOrigin(false)("given (x) -> x + 1"))
    |> (given (_) -> expectOrigin(false)("given (x) -> x == 1"))
    |> (given (_) -> expectOrigin(false)("given (x) -> \"a\" != x"))
    |> (given (_) -> expectOrigin(false)("given (x) -> x < 1 && x >= 0"))
    |> (given (_) -> expectOrigin(false)("given (x) -> point.x + other(x)"))
    |> (given (_) -> expectOrigin(false)("given (x) -> match x with | Some(y) -> y | None -> 0"))
    |> (given (_) -> expectOrigin(false)("given (x) -> let step = given (y) -> y in step(x)"))
    |> (given (_) -> expectOrigin(false)("given (x) -> x |> Ashes.Text.fromInt"))

let checkOperatorOrigin unit =
    Unit
    |> (given (_) -> expectOrigin(true)("given (x) -> given (y) -> x == y"))
    |> (given (_) -> expectOrigin(true)("given (x) -> given (y) -> x != y"))
    |> (given (_) -> expectOrigin(true)("given (x) -> given (y) -> if x <= y then x else y"))
    |> (given (_) -> expectOrigin(true)("given (x) -> !x"))
    |> (given (_) -> expectOrigin(true)("given (x) -> match x with | Some(y) when y == x -> y | _ -> 0"))
    |> (given (_) -> expectOrigin(true)("given (x) -> let step = given (y) -> y > x in step(x)"))
    |> (given (_) -> expectOrigin(true)("given (x) -> x == 1 || [x] == []"))

let checkReferenceOrigin unit =
    Unit
    |> (given (_) -> expectOrigin(true)("given (x) -> sameAs(x)(x)"))
    |> (given (_) -> expectOrigin(true)("given (x) -> Eq.equal(x)(x)"))
    |> (given (_) -> expectOrigin(true)("given (x) -> ordering.compare"))
    |> (given (_) -> expectOrigin(true)("given (x) -> if x then [sameAs] else []"))
    |> (given (_) -> expectOrigin(true)("given (x) -> (x, Holder(value = sameAs))"))
    |> (given (_) -> expectOrigin(true)("given (sameAs) -> sameAs"))

let runOperatorConstraintOriginsTests unit =
    Unit
    |> checkNoOrigin
    |> checkOperatorOrigin
    |> checkReferenceOrigin
