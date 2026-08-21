import Ashes.Test as test
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import TypeInferenceTests
let expectMatchType expected expression =
    match Unit
    |> emptyTypeEnvironment
    |> inferExpression(expression) with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, constraints = _constraints, error = None } ->
            let actual =
                semanticType
                |> applySubstitution(substitution)
                |> formatSemanticType
            in
                if actual == expected
                then Unit
                else test.fail("expected " + expected + " but inferred " + actual)
        | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(error) } ->
            test.fail(
                "match inference should succeed: " + Ashes.Trait.Show.show(error)
            )

let expectBasicPatterns unit =
    unit
    |> (given (_) ->
        None
        |> ExprMatch(ExprInt(42))([(PatternVar("value"), ExprVar("value"), None)])
        |> expectMatchType("Int"))
    |> (given (_) ->
        None
        |> ExprMatch(
            ExprList([ExprString("head")])(false),
            [(PatternCons(PatternVar("head"))(PatternWildcard), ExprVar("head"), None)]
        )
        |> expectMatchType("Str"))
    |> (given (_) ->
        None
        |> ExprMatch(
            ExprTuple([ExprInt(1), ExprString("value")]),
            [(PatternTuple([PatternWildcard, PatternVar("text")]), ExprVar("text"), None)]
        )
        |> expectMatchType("Str"))
    |> (given (_) ->
        None
        |> ExprMatch(ExprInt(7))([(PatternAs(PatternWildcard)("whole"), ExprVar("whole"), None)])
        |> expectMatchType("Int"))

let expectGuardedAndConstrainedPatterns unit =
    unit
    |> (given (_) ->
        None
        |> ExprMatch(
            ExprInt(1),
            [(PatternInt(1), ExprString("one"), Some(ExprBool(true))), (PatternWildcard, ExprString("other"), None)]
        )
        |> expectMatchType("Str"))
    |> (given (_) ->
        None
        |> ExprMatch(ExprInt(1))([(PatternVar("value"), ExprAdd(ExprVar("value"))(ExprInt(2)), None)])
        |> TypeInferenceTests.expectConstraint("Add")("Int"))

let rejectMismatchedPatternBranches unit =
    (let expression =
        ExprMatch(
            ExprBool(true),
            [(PatternBool(true), ExprInt(1), None), (PatternBool(false), ExprString("wrong"), None)],
            None
        )
    in
        match Unit
        |> emptyTypeEnvironment
        |> inferExpression(expression) with
            | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(InferenceUnificationError(TypeMismatch(_left, _right))) } -> Unit
            | _ -> test.fail("match branches should have one result type"))

let rejectInvalidPatternGuard unit =
    (let expression = ExprMatch(ExprInt(1))([(PatternWildcard, ExprInt(1), Some(ExprString("wrong")))])(None)
    in
        match Unit
        |> emptyTypeEnvironment
        |> inferExpression(expression) with
            | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(InferenceUnificationError(TypeMismatch(_left, _right))) } -> Unit
            | _ -> test.fail("match guards should be Bool"))

let rejectDuplicatePatternBinding unit =
    (let expression =
        ExprMatch(
            ExprTuple([ExprInt(1), ExprInt(2)]),
            [(PatternTuple([PatternVar("value"), PatternVar("value")]), ExprInt(0), None)],
            None
        )
    in
        match Unit
        |> emptyTypeEnvironment
        |> inferExpression(expression) with
            | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(DuplicatePatternBinding("value")) } -> Unit
            | _ -> test.fail("duplicate pattern bindings should be rejected"))

let runPatternInferenceTests unit =
    unit
    |> expectBasicPatterns
    |> expectGuardedAndConstrainedPatterns
    |> rejectMismatchedPatternBranches
    |> rejectInvalidPatternGuard
    |> rejectDuplicatePatternBinding
    |> (given (_) -> Ashes.IO.print("all self-hosted pattern inference tests passed"))
