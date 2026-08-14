import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.ProgramInference
let pointType = TypeDecl(name = "Point", typeParameters = [], constructors = [TypeConstructor(name = "Point", parameters = [TypeNamed("Int"), TypeNamed("Str")], fieldNames = ["x", "label"])], isRecord = true, derivingTraits = [])

let pointValue = ExprRecord("Point")([("label", ExprString("origin")), ("x", ExprInt(1))])

let expectPoint expression =
    match inferProgram(ProgramSyntax(items = [TopLevelType(pointType)], body = Some(expression))) with
        | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
            if formatSemanticType(applySubstitution(substitution)(semanticType)) == "Point"
            then Unit
            else test.fail("record expression should infer Point")
        | _ -> test.fail("record expression should infer")

let runRecordInferenceTests unit =
    (let literalChecked = expectPoint(pointValue)
    in
        let updateChecked = expectPoint(ExprRecordUpdate(pointValue)([("label", ExprString("updated"))]))
        in
            let missingField = ExprRecord("Point")([("x", ExprInt(1))])
            in
                let missingChecked =
                    match inferProgram(ProgramSyntax(items = [TopLevelType(pointType)], body = Some(missingField))) with
                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(MissingRecordField("Point", "label"))) } -> Unit
                        | _ -> test.fail("record literals should require every field")
                in
                    let unknownUpdate = ExprRecordUpdate(pointValue)([("missing", ExprInt(1))])
                    in
                        let unknownChecked =
                            match inferProgram(ProgramSyntax(items = [TopLevelType(pointType)], body = Some(unknownUpdate))) with
                                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(UnknownRecordField("Point", "missing"))) } -> Unit
                                | _ -> test.fail("record updates should reject unknown fields")
                        in
                            let duplicateField = ExprRecord("Point")([("x", ExprInt(1)), ("x", ExprInt(2)), ("label", ExprString("origin"))])
                            in
                                let duplicateChecked =
                                    match inferProgram(ProgramSyntax(items = [TopLevelType(pointType)], body = Some(duplicateField))) with
                                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(DuplicateRecordField("x"))) } -> Unit
                                        | _ -> test.fail("record fields should not be repeated")
                                in
                                    let wrongFieldType = ExprRecord("Point")([("x", ExprString("wrong")), ("label", ExprString("origin"))])
                                    in
                                        let fieldTypeChecked =
                                            match inferProgram(ProgramSyntax(items = [TopLevelType(pointType)], body = Some(wrongFieldType))) with
                                                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(_left, _right)))) } -> Unit
                                                | _ -> test.fail("record field values should match their declared types")
                                        in Ashes.IO.print("all self-hosted record inference tests passed"))
