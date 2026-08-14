import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.TypeInference
let clockDeclaration = CapabilityDecl(name = "Clock", typeParameters = [], operations = [CapabilityOperation(name = "now", signature = Some(TypeArrow(TypeUnit)(TypeNamed("Int"))([])(None)))])

let stateDeclaration = CapabilityDecl(name = "State", typeParameters = [TypeParameter(name = "a")], operations = [CapabilityOperation(name = "get", signature = Some(TypeArrow(TypeUnit)(TypeNamed("a"))([])(None))), CapabilityOperation(name = "set", signature = Some(TypeArrow(TypeNamed("a"))(TypeUnit)([])(None)))])

let selectDeclaration = CapabilityDecl(name = "Select", typeParameters = [TypeParameter(name = "a")], operations = [CapabilityOperation(name = "less", signature = Some(TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None))([])(None)))])

let logDeclaration = CapabilityDecl(name = "Log", typeParameters = [], operations = [CapabilityOperation(name = "write", signature = Some(TypeArrow(TypeNamed("Str"))(TypeUnit)([])(None)))])

let inferredLogDeclaration = CapabilityDecl(name = "Log", typeParameters = [], operations = [CapabilityOperation(name = "write", signature = None)])

let recursive rowContainsCapability name semanticType =
    match semanticType with
        | SemRow(capabilities, tail) ->
            let recursive capabilitiesContain values =
                match values with
                    | [] -> false
                    | SemCapability(candidateName, _arguments) :: rest ->
                        if name == candidateName
                        then true
                        else capabilitiesContain(rest)
                    | _ :: rest -> capabilitiesContain(rest)
            in
                if capabilitiesContain(capabilities)
                then true
                else
                    match tail with
                        | None -> false
                        | Some(tailType) -> rowContainsCapability(name)(tailType)
        | _ -> false

let recursive rowIsOpen semanticType =
    match semanticType with
        | SemVariable(_) -> true
        | SemRow(_capabilities, Some(tail)) -> rowIsOpen(tail)
        | _ -> false

let expectLambdaCapability capabilityName declarations expression =
    match inferProgram(ProgramSyntax(items = declarations, body = Some(expression))) with
        | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
            match applySubstitution(substitution)(semanticType) with
                | SemFunction(_argument, _result, Some(row)) ->
                    if rowContainsCapability(capabilityName)(row)
                    then
                        if rowIsOpen(row)
                        then Unit
                        else test.fail("inferred capability rows should remain open")
                    else test.fail("lambda should require " + capabilityName)
                | _ -> test.fail("capability expression should infer a function")
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("capability expression should infer: " + Ashes.Trait.Show.show(error))

let expectClockOperation unit =
    match inferProgram(ProgramSyntax(items = [TopLevelCapability(clockDeclaration)], body = Some(ExprQualifiedVar("Clock")("now")))) with
        | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
            let actual = formatSemanticType(applySubstitution(substitution)(semanticType))
            in test.assertEqual("() -> Int needs {Clock}")(actual)
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("declared operation should resolve: " + Ashes.Trait.Show.show(error))

let expectParameterizedOperation unit =
    match inferProgram(ProgramSyntax(items = [TopLevelCapability(stateDeclaration)], body = Some(ExprQualifiedVar("State")("get")))) with
        | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
            match applySubstitution(substitution)(semanticType) with
                | SemFunction(argumentType, SemVariable(resultId), Some(rowType)) ->
                    match (argumentType, rowType) with
                        | (SemTuple([]), SemRow(capabilities, None)) ->
                            match capabilities with
                                | SemCapability("State", arguments) :: [] ->
                                    match arguments with
                                        | SemVariable(capabilityId) :: [] -> test.assertEqual(resultId)(capabilityId)
                                        | _ -> test.fail("State should have one inferred type argument")
                                | _ -> test.fail("operation should require State")
                        | _ -> test.fail("State.get should take Unit and have a closed capability row")
                | other -> test.fail("parameterized operation should share its capability argument: " + formatSemanticType(other))
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("parameterized operation should resolve: " + Ashes.Trait.Show.show(error))

let expectDuplicateCapability unit =
    match inferProgram(ProgramSyntax(items = [TopLevelCapability(clockDeclaration), TopLevelCapability(clockDeclaration)], body = None)) with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(DuplicateCapabilityDeclaration("Clock")) } -> Unit
        | _ -> test.fail("duplicate capability declarations should be rejected")

let expectInnermostArrow unit =
    match inferProgram(ProgramSyntax(items = [TopLevelCapability(selectDeclaration)], body = Some(ExprQualifiedVar("Select")("less")))) with
        | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
            match applySubstitution(substitution)(semanticType) with
                | SemFunction(SemVariable(firstId), SemFunction(SemVariable(secondId), SemBool, Some(SemRow(capabilities, None))), None) ->
                    match capabilities with
                        | SemCapability("Select", arguments) :: [] ->
                            match arguments with
                                | SemVariable(capabilityId) :: [] ->
                                    if firstId == secondId
                                    then test.assertEqual(secondId)(capabilityId)
                                    else test.fail("curried operation parameters should share one type")
                                | _ -> test.fail("Select should have one inferred type argument")
                        | _ -> test.fail("innermost arrow should require Select")
                | _ -> test.fail("only the innermost curried operation arrow should carry its effect")
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("curried operation should resolve: " + Ashes.Trait.Show.show(error))

let expectReservedCapability unit =
    (let reserved = CapabilityDecl(name = "Entropy", typeParameters = [], operations = [])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelCapability(reserved)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ReservedCapabilityDeclaration("Entropy")) } -> Unit
            | _ -> test.fail("built-in runtime capability names should stay reserved"))

let expectDuplicateOperation unit =
    (let duplicate = CapabilityDecl(name = "Clock", typeParameters = [], operations = [CapabilityOperation(name = "now", signature = Some(TypeArrow(TypeUnit)(TypeNamed("Int"))([])(None))), CapabilityOperation(name = "now", signature = Some(TypeArrow(TypeUnit)(TypeNamed("Int"))([])(None)))])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelCapability(duplicate)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(DuplicateCapabilityOperation("Clock", "now")) } -> Unit
            | _ -> test.fail("duplicate capability operations should be rejected"))

let expectParameterizedSignature unit =
    (let missing = CapabilityDecl(name = "State", typeParameters = [TypeParameter(name = "a")], operations = [CapabilityOperation(name = "get", signature = None)])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelCapability(missing)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ParameterizedCapabilityOperationRequiresSignature("State", "get")) } -> Unit
            | _ -> test.fail("parameterized operations should require signatures"))

let expectFunctionSignature unit =
    (let invalid = CapabilityDecl(name = "Clock", typeParameters = [], operations = [CapabilityOperation(name = "now", signature = Some(TypeNamed("Int")))])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelCapability(invalid)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(CapabilityOperationRequiresFunction("Clock", "now")) } -> Unit
            | _ -> test.fail("capability operation signatures should be functions"))

let expectCapabilityEffects unit =
    (let clockCall = ExprCall(ExprQualifiedVar("Clock")("now"))(ExprTuple([]))(false)
    in
        let implicitLambda = ExprLambda("ignored")(clockCall)(None)
        in
            let implicitChecked = expectLambdaCapability("Clock")([TopLevelCapability(clockDeclaration)])(implicitLambda)
            in
                let explicitLambda = ExprLambda("ignored")(ExprPerform(clockCall))(None)
                in
                    let explicitChecked = expectLambdaCapability("Clock")([TopLevelCapability(clockDeclaration)])(explicitLambda)
                    in
                        let logCall = ExprCall(ExprQualifiedVar("Log")("write"))(ExprString("message"))(false)
                        in
                            let combinedLambda = ExprLambda("ignored")(ExprTuple([clockCall, logCall]))(None)
                            in
                                let clockUnionChecked = expectLambdaCapability("Clock")([TopLevelCapability(clockDeclaration), TopLevelCapability(logDeclaration)])(combinedLambda)
                                in
                                    let logUnionChecked = expectLambdaCapability("Log")([TopLevelCapability(clockDeclaration), TopLevelCapability(logDeclaration)])(combinedLambda)
                                    in
                                        let higherOrder = ExprLambda("operation")(ExprCall(ExprVar("operation"))(ExprTuple([]))(false))(None)
                                        in
                                            let higherOrderChecked =
                                                match inferExpression(higherOrder)(emptyTypeEnvironment(Unit)) with
                                                    | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, constraints = _constraints, error = None } ->
                                                        match applySubstitution(substitution)(semanticType) with
                                                            | SemFunction(SemFunction(SemTuple([]), SemVariable(parameterResultId), Some(parameterRow)), SemVariable(bodyResultId), Some(bodyRow)) ->
                                                                if parameterResultId == bodyResultId
                                                                then test.assertEqual(parameterRow)(bodyRow)
                                                                else test.fail("higher-order call result should flow through")
                                                            | _ -> test.fail("higher-order effects should remain row-polymorphic")
                                                    | _ -> test.fail("higher-order effect expression should infer")
                                            in
                                                let inferredLogLambda = ExprLambda("ignored")(ExprCall(ExprQualifiedVar("Log")("write"))(ExprString("message"))(false))(None)
                                                in
                                                    let inferredLogChecked = expectLambdaCapability("Log")([TopLevelCapability(inferredLogDeclaration)])(inferredLogLambda)
                                                    in
                                                        let resultType = SemNamed(-1)("Result")([SemInt, SemString])
                                                        in
                                                            let effectfulMapper = SemFunction(SemString)(SemBool)(Some(SemRow([SemCapability("Clock")([])])(None)))
                                                            in
                                                                let resultEnvironment = addTypeBinding("mapper")(TypeScheme(quantified = [], body = effectfulMapper, constraints = []))(addTypeBinding("input")(TypeScheme(quantified = [], body = resultType, constraints = []))(emptyTypeEnvironment(Unit)))
                                                                in
                                                                    let resultLambda = ExprLambda("ignored")(ExprResultPipe(ExprVar("input"))(ExprVar("mapper")))(None)
                                                                    in
                                                                        match inferExpression(resultLambda)(resultEnvironment) with
                                                                            | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, constraints = _constraints, error = None } ->
                                                                                match applySubstitution(substitution)(semanticType) with
                                                                                    | SemFunction(_argument, _result, Some(row)) ->
                                                                                        if rowContainsCapability("Clock")(row)
                                                                                        then Unit
                                                                                        else test.fail("Result mapper effects should propagate")
                                                                                    | _ -> test.fail("Result pipeline wrapper should infer a function")
                                                                            | _ -> test.fail("effectful Result mapper should infer"))

let expectClosedRowAnnotations unit =
    (let clockCall = ExprCall(ExprQualifiedVar("Clock")("now"))(ExprTuple([]))(false)
    in
        let bindingValue = ExprLambda("ignored")(clockCall)(None)
        in
            let clockAnnotation = TypeArrow(TypeUnit)(TypeNamed("Int"))([("Clock", [])])(None)
            in
                let acceptedBinding = LetBindingSyntax(name = "readClock", value = bindingValue, sugarParameters = [], typeAnnotation = Some(clockAnnotation), requirements = [])
                in
                    let acceptedChecked =
                        match inferProgram(ProgramSyntax(items = [TopLevelCapability(clockDeclaration), TopLevelLet(acceptedBinding)(false)], body = Some(ExprVar("readClock")))) with
                            | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
                                match applySubstitution(substitution)(semanticType) with
                                    | SemFunction(SemTuple([]), SemInt, Some(row)) ->
                                        if rowContainsCapability("Clock")(row)
                                        then
                                            if rowIsOpen(row)
                                            then test.fail("written closed capability annotation should not remain open")
                                            else Unit
                                        else test.fail("closed annotation should retain Clock")
                                    | _ -> test.fail("closed capability annotation should retain its function type")
                            | _ -> test.fail("matching closed capability annotation should infer")
                    in
                        let logAnnotation = TypeArrow(TypeUnit)(TypeNamed("Int"))([("Log", [])])(None)
                        in
                            let rejectedBinding = LetBindingSyntax(name = "readClock", value = bindingValue, sugarParameters = [], typeAnnotation = Some(logAnnotation), requirements = [])
                            in
                                match inferProgram(ProgramSyntax(items = [TopLevelCapability(clockDeclaration), TopLevelCapability(logDeclaration), TopLevelLet(rejectedBinding)(false)], body = None)) with
                                    | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(_left, _right)))) } -> Unit
                                    | _ -> test.fail("closed capability annotations should reject undeclared effects"))

let expectInvalidPerform unit =
    (let invalidChecked =
        match inferExpression(ExprPerform(ExprInt(42)))(emptyTypeEnvironment(Unit)) with
            | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(PerformRequiresCapabilityOperation) } -> Unit
            | _ -> test.fail("perform should require a capability operation call")
    in
        let missingCall = ExprPerform(ExprCall(ExprQualifiedVar("Clock")("missing"))(ExprTuple([]))(false))
        in
            match inferProgram(ProgramSyntax(items = [TopLevelCapability(clockDeclaration)], body = Some(missingCall))) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(UnknownCapabilityOperation("Clock", "missing"))) } -> Unit
                | _ -> test.fail("perform should reject unknown capability operations"))

let expectOperationValueRules unit =
    (let unsignedChecked =
        match inferProgram(ProgramSyntax(items = [TopLevelCapability(inferredLogDeclaration)], body = Some(ExprQualifiedVar("Log")("write")))) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(UnsignedCapabilityOperationRequiresSignature("Log", "write"))) } -> Unit
            | _ -> test.fail("unsigned operations should not be first-class values")
    in
        let missingCall = ExprCall(ExprQualifiedVar("Clock")("missing"))(ExprTuple([]))(false)
        in
            match inferProgram(ProgramSyntax(items = [TopLevelCapability(clockDeclaration)], body = Some(missingCall))) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(UnknownCapabilityOperation("Clock", "missing"))) } -> Unit
                | _ -> test.fail("implicit calls should reject unknown capability operations"))

let runCapabilityInferenceTests unit =
    (let clockChecked = expectClockOperation(Unit)
    in
        let parameterizedChecked = expectParameterizedOperation(Unit)
        in
            let duplicateCapabilityChecked = expectDuplicateCapability(Unit)
            in
                let innermostArrowChecked = expectInnermostArrow(Unit)
                in
                    let reservedCapabilityChecked = expectReservedCapability(Unit)
                    in
                        let duplicateOperationChecked = expectDuplicateOperation(Unit)
                        in
                            let parameterizedSignatureChecked = expectParameterizedSignature(Unit)
                            in
                                let functionSignatureChecked = expectFunctionSignature(Unit)
                                in
                                    let capabilityEffectsChecked = expectCapabilityEffects(Unit)
                                    in
                                        let closedRowAnnotationsChecked = expectClosedRowAnnotations(Unit)
                                        in
                                            let invalidPerformChecked = expectInvalidPerform(Unit)
                                            in
                                                let operationValueRulesChecked = expectOperationValueRules(Unit)
                                                in Ashes.IO.print("all self-hosted capability effect inference tests passed"))
