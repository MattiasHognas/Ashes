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

let runRecordInferenceTests unit =
    unit
    |> expectValidRecords
    |> rejectMissingRecordField
    |> rejectUnknownRecordField
    |> rejectDuplicateRecordField
    |> rejectWrongRecordFieldType
    |> (given (_) -> Ashes.IO.print("all self-hosted record inference tests passed"))
