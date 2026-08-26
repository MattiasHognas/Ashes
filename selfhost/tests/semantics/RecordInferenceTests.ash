import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.ProgramInference
let pointType =
    TypeDecl(name = "Point", typeParameters = [], constructors = [TypeConstructor(name = "Point", parameters = [TypeNamed(
        "Int"
    ), TypeNamed(
        "Str"
    )], fieldNames = ["x", "label"])], isRecord = true, derivingTraits = [])

let pointValue = ExprRecord("Point")([("label", ExprString("origin")), ("x", ExprInt(1))])(false)

let expectPoint expression =
    match inferProgram(ProgramSyntax(items = [TopLevelType(pointType)], body = Some(expression))) with
        | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
            if formatSemanticType(applySubstitution(substitution)(semanticType)) == "Point"
            then Unit
            else test.fail("record expression should infer Point")
        | _ -> test.fail("record expression should infer")

let inferPointExpression expression =
    inferProgram(
        ProgramSyntax(items = [TopLevelType(pointType)], body = Some(expression))
    )

let expectValidRecords unit =
    unit
    |> (given (_) -> expectPoint(pointValue))
    |> (given (_) ->
        [("label", ExprString("updated"))]
        |> ExprRecordUpdate(pointValue)
        |> expectPoint)

let rejectMissingRecordField unit =
    match [("x", ExprInt(1))]
    |> (given (fields) -> ExprRecord("Point")(fields)(false))
    |> inferPointExpression with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(MissingRecordField("Point", "label"))) } -> Unit
        | _ -> test.fail("record literals should require every field")

let rejectUnknownRecordField unit =
    match [("missing", ExprInt(1))]
    |> ExprRecordUpdate(pointValue)
    |> inferPointExpression with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(UnknownRecordField("Point", "missing"))) } -> Unit
        | _ -> test.fail("record updates should reject unknown fields")

let rejectDuplicateRecordField unit =
    match [("x", ExprInt(1)), ("x", ExprInt(2)), ("label", ExprString("origin"))]
    |> (given (fields) -> ExprRecord("Point")(fields)(false))
    |> inferPointExpression with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(DuplicateRecordField("x"))) } -> Unit
        | _ -> test.fail("record fields should not be repeated")

let rejectWrongRecordFieldType unit =
    match [("x", ExprString("wrong")), ("label", ExprString("origin"))]
    |> (given (fields) -> ExprRecord("Point")(fields)(false))
    |> inferPointExpression with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(_left, _right)))) } -> Unit
        | _ -> test.fail("record field values should match their declared types")

let optionType =
    TypeDecl(
        name = "Option",
        typeParameters = [],
        constructors = [TypeConstructor(name = "NoVal", parameters = [], fieldNames = []), TypeConstructor(name = "HasVal", parameters = [TypeNamed("Int")], fieldNames = [])],
        isRecord = false,
        derivingTraits = []
    )

let inferOptionExpression expression = inferProgram(ProgramSyntax(items = [TopLevelType(optionType)], body = Some(expression)))

let bareNullaryMatch =
    ExprMatch(
        ExprCall(ExprVar("HasVal"))(ExprInt(1))(false)(callArgumentsInline),
        [(PatternVar("NoVal"), ExprInt(0), None), (PatternConstructor("HasVal")([PatternVar("n")]), ExprVar("n"), None)],
        None
    )

let expectBareNullaryConstructorPatternIsAConstructor unit =
    match inferOptionExpression(bareNullaryMatch) with
        | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
            if formatSemanticType(applySubstitution(substitution)(semanticType)) == "Int"
            then Unit
            else test.fail("a bare nullary constructor pattern must type the match like its constructor")
        | _ -> test.fail("a bare nullary constructor pattern must infer")

let rejectBareNullaryConstructorAgainstAnotherType unit =
    match None
    |> ExprMatch(ExprInt(1))([(PatternVar("NoVal"), ExprInt(0), None), (PatternWildcard, ExprInt(1), None)])
    |> inferOptionExpression with
        | ProgramInferenceResult { error = Some(_error) } -> Unit
        | _ -> test.fail("a bare nullary constructor pattern is not a binder and must not match a scrutinee of another type")

let expectPlainNameStaysABinder unit =
    match None
    |> ExprMatch(ExprInt(1))([(PatternVar("anything"), ExprVar("anything"), None)])
    |> inferOptionExpression with
        | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
            if formatSemanticType(applySubstitution(substitution)(semanticType)) == "Int"
            then Unit
            else test.fail("a name that is not a constructor still binds the scrutinee")
        | _ -> test.fail("a plain variable pattern must infer")

let optionCall (value: Int) = ExprCall(ExprVar("HasVal"))(ExprInt(value))(false)(callArgumentsInline)

let expectCoverageError expression (expected: TypeInferenceError) (label: Str) =
    match inferOptionExpression(expression) with
        | ProgramInferenceResult { error = Some(error) } ->
            if error == ProgramExpressionError(expected)
            then Unit
            else test.fail(label + ": unexpected diagnostic " + Ashes.Trait.Show.show(error))
        | _ -> test.fail(label + ": the match must be rejected")

let expectCovered expression (label: Str) =
    match inferOptionExpression(expression) with
        | ProgramInferenceResult { error = None } -> Unit
        | ProgramInferenceResult { error = Some(error) } -> test.fail(label + ": " + Ashes.Trait.Show.show(error))

let rejectMissingConstructorArm unit =
    expectCoverageError(
        ExprMatch(optionCall(1))([(PatternConstructor("HasVal")([PatternVar("n")]), ExprVar("n"), None)])(None)
    )(
        NonExhaustiveMatch("Non-exhaustive match expression. Missing constructor(s): 'NoVal'.")
    )(
        "rejectMissingConstructorArm"
    )

let rejectMissingNestedCase unit =
    expectCoverageError(
        ExprMatch(optionCall(1))([(PatternConstructor("HasVal")([PatternInt(1)]), ExprInt(1), None), (PatternVar("NoVal"), ExprInt(0), None)])(None)
    )(
        NonExhaustiveMatch("Non-exhaustive match expression. Missing case: HasVal(_).")
    )(
        "rejectMissingNestedCase"
    )

let rejectArmAfterCatchAll unit =
    expectCoverageError(
        ExprMatch(optionCall(1))([(PatternWildcard, ExprInt(0), None), (PatternVar("NoVal"), ExprInt(1), None)])(None)
    )(
        UnreachableMatchArm("Unreachable match arm: a catch-all pattern was already matched earlier.")
    )(
        "rejectArmAfterCatchAll"
    )

let rejectRepeatedLiteralArm unit =
    expectCoverageError(
        ExprMatch(ExprInt(1))([(PatternInt(1), ExprInt(1), None), (PatternInt(1), ExprInt(2), None), (PatternWildcard, ExprInt(0), None)])(None)
    )(
        UnreachableMatchArm("Unreachable match arm: integer literal 1 is already matched earlier.")
    )(
        "rejectRepeatedLiteralArm"
    )

let rejectListWithoutCons unit =
    expectCoverageError(
        ExprMatch(ExprList([ExprInt(1)])(false))([(PatternEmptyList, ExprInt(0), None)])(None)
    )(
        NonExhaustiveMatch("Non-exhaustive match expression. Missing case: x :: xs.")
    )(
        "rejectListWithoutCons"
    )

let rejectHalfCoveredBool unit =
    expectCoverageError(
        ExprMatch(ExprBool(true))([(PatternBool(true), ExprInt(1), None)])(None)
    )(
        NonExhaustiveMatch("Non-exhaustive match expression.")
    )(
        "rejectHalfCoveredBool"
    )

let acceptCompleteMatches unit =
    unit
    |> (given (_) ->
        expectCovered(
            ExprMatch(optionCall(1))([(PatternConstructor("HasVal")([PatternVar("n")]), ExprVar("n"), None), (PatternVar("NoVal"), ExprInt(0), None)])(None)
        )(
            "every constructor named"
        ))
    |> (given (_) ->
        expectCovered(
            ExprMatch(ExprList([ExprInt(1)])(false))([(PatternEmptyList, ExprInt(0), None), (PatternCons(PatternVar("x"))(PatternWildcard), ExprVar("x"), None)])(None)
        )(
            "empty list and cons"
        ))
    |> (given (_) ->
        expectCovered(
            ExprMatch(ExprBool(true))([(PatternBool(true), ExprInt(1), None), (PatternBool(false), ExprInt(0), None)])(None)
        )(
            "both bool literals"
        ))
    |> (given (_) ->
        expectCovered(
            ExprMatch(optionCall(1))([(PatternConstructor("HasVal")([PatternInt(1)]), ExprInt(1), Some(ExprBool(true))), (PatternWildcard, ExprInt(0), None)])(None)
        )(
            "guarded arm plus catch-all"
        ))

let runRecordInferenceTests unit =
    unit
    |> expectValidRecords
    |> rejectMissingRecordField
    |> rejectUnknownRecordField
    |> rejectDuplicateRecordField
    |> rejectWrongRecordFieldType
    |> (given (_) -> expectBareNullaryConstructorPatternIsAConstructor(Unit))
    |> (given (_) -> rejectBareNullaryConstructorAgainstAnotherType(Unit))
    |> (given (_) -> expectPlainNameStaysABinder(Unit))
    |> (given (_) -> rejectMissingConstructorArm(Unit))
    |> (given (_) -> rejectMissingNestedCase(Unit))
    |> (given (_) -> rejectArmAfterCatchAll(Unit))
    |> (given (_) -> rejectRepeatedLiteralArm(Unit))
    |> (given (_) -> rejectListWithoutCons(Unit))
    |> (given (_) -> rejectHalfCoveredBool(Unit))
    |> (given (_) -> acceptCompleteMatches(Unit))
    |> (given (_) -> Ashes.IO.print("all self-hosted record inference tests passed"))
