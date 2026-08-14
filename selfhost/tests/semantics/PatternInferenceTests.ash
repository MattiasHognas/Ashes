import Ashes.Test as test
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import TypeInferenceTests
let expectMatchType expected expression =
    match inferExpression(expression)(emptyTypeEnvironment(Unit)) with
        | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, constraints = _constraints, error = None } ->
            let actual = formatSemanticType(applySubstitution(substitution)(semanticType))
            in
                if actual == expected
                then Unit
                else test.fail("expected " + expected + " but inferred " + actual)
        | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(error) } -> test.fail("match inference should succeed: " + Ashes.Trait.Show.show(error))

let runPatternInferenceTests unit =
    (let variableMatch = ExprMatch(ExprInt(42))([(PatternVar("value"), ExprVar("value"), None)])(None)
    in
        let variableChecked = expectMatchType("Int")(variableMatch)
        in
            let consMatch = ExprMatch(ExprList([ExprString("head")]))([(PatternCons(PatternVar("head"))(PatternWildcard), ExprVar("head"), None)])(None)
            in
                let consChecked = expectMatchType("Str")(consMatch)
                in
                    let tupleMatch = ExprMatch(ExprTuple([ExprInt(1), ExprString("value")]))([(PatternTuple([PatternWildcard, PatternVar("text")]), ExprVar("text"), None)])(None)
                    in
                        let tupleChecked = expectMatchType("Str")(tupleMatch)
                        in
                            let aliasMatch = ExprMatch(ExprInt(7))([(PatternAs(PatternWildcard)("whole"), ExprVar("whole"), None)])(None)
                            in
                                let aliasChecked = expectMatchType("Int")(aliasMatch)
                                in
                                    let guardedMatch = ExprMatch(ExprInt(1))([(PatternInt(1), ExprString("one"), Some(ExprBool(true))), (PatternWildcard, ExprString("other"), None)])(None)
                                    in
                                        let guardChecked = expectMatchType("Str")(guardedMatch)
                                        in
                                            let constrainedBody = ExprMatch(ExprInt(1))([(PatternVar("value"), ExprAdd(ExprVar("value"))(ExprInt(2)), None)])(None)
                                            in
                                                let constraintChecked = TypeInferenceTests.expectConstraint("Add")("Int")(constrainedBody)
                                                in
                                                    let mismatchedBranches = ExprMatch(ExprBool(true))([(PatternBool(true), ExprInt(1), None), (PatternBool(false), ExprString("wrong"), None)])(None)
                                                    in
                                                        let branchMismatchChecked =
                                                            match inferExpression(mismatchedBranches)(emptyTypeEnvironment(Unit)) with
                                                                | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(InferenceUnificationError(TypeMismatch(_left, _right))) } -> Unit
                                                                | _ -> test.fail("match branches should have one result type")
                                                        in
                                                            let invalidGuard = ExprMatch(ExprInt(1))([(PatternWildcard, ExprInt(1), Some(ExprString("wrong")))])(None)
                                                            in
                                                                let guardMismatchChecked =
                                                                    match inferExpression(invalidGuard)(emptyTypeEnvironment(Unit)) with
                                                                        | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(InferenceUnificationError(TypeMismatch(_left, _right))) } -> Unit
                                                                        | _ -> test.fail("match guards should be Bool")
                                                                in
                                                                    let duplicateBinding = ExprMatch(ExprTuple([ExprInt(1), ExprInt(2)]))([(PatternTuple([PatternVar("value"), PatternVar("value")]), ExprInt(0), None)])(None)
                                                                    in
                                                                        let duplicateChecked =
                                                                            match inferExpression(duplicateBinding)(emptyTypeEnvironment(Unit)) with
                                                                                | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(DuplicatePatternBinding("value")) } -> Unit
                                                                                | _ -> test.fail("duplicate pattern bindings should be rejected")
                                                                        in Ashes.IO.print("all self-hosted pattern inference tests passed"))
