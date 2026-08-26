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
    |> (given (_) -> Ashes.IO.print("all self-hosted record inference tests passed"))
