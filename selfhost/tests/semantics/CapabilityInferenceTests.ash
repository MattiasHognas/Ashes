import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.TypeInference
let clockDeclaration =
    CapabilityDecl(name = "Clock", typeParameters = [], operations = [CapabilityOperation(name = "now", signature = None
    |> TypeArrow(TypeUnit)(TypeNamed("Int"))([])
    |> Some)])

let stateDeclaration =
    CapabilityDecl(name = "State", typeParameters = [TypeParameter(name = "a")], operations = [CapabilityOperation(name = "get", signature = None
    |> TypeArrow(TypeUnit)(TypeNamed("a"))([])
    |> Some), CapabilityOperation(name = "set", signature = None
    |> TypeArrow(TypeNamed("a"))(TypeUnit)([])
    |> Some)])

let selectDeclaration =
    CapabilityDecl(name = "Select", typeParameters = [TypeParameter(name = "a")], operations = [CapabilityOperation(name = "less", signature = None
    |> TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None))([])
    |> Some)])

let logDeclaration =
    CapabilityDecl(name = "Log", typeParameters = [], operations = [CapabilityOperation(name = "write", signature = None
    |> TypeArrow(TypeNamed("Str"))(TypeUnit)([])
    |> Some)])

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
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } ->
            test.fail(
                "capability expression should infer: " + Ashes.Trait.Show.show(error)
            )

let expectClockOperation unit =
    match inferProgram(ProgramSyntax(items = [TopLevelCapability(clockDeclaration)], body = "now"
    |> ExprQualifiedVar("Clock")
    |> Some)) with
        | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
            let actual =
                semanticType
                |> applySubstitution(substitution)
                |> formatSemanticType
            in test.assertEqual("() -> Int needs {Clock}")(actual)
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } ->
            test.fail(
                "declared operation should resolve: " + Ashes.Trait.Show.show(error)
            )

let expectParameterizedOperation unit =
    match inferProgram(ProgramSyntax(items = [TopLevelCapability(stateDeclaration)], body = "get"
    |> ExprQualifiedVar("State")
    |> Some)) with
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
                | other ->
                    test.fail(
                        "parameterized operation should share its capability argument: " + formatSemanticType(other)
                    )
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } ->
            test.fail(
                "parameterized operation should resolve: " + Ashes.Trait.Show.show(error)
            )

let expectDuplicateCapability unit =
    match inferProgram(
        ProgramSyntax(items = [TopLevelCapability(clockDeclaration), TopLevelCapability(clockDeclaration)], body = None)
    ) with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(DuplicateCapabilityDeclaration("Clock")) } -> Unit
        | _ -> test.fail("duplicate capability declarations should be rejected")

let expectInnermostArrow unit =
    match inferProgram(ProgramSyntax(items = [TopLevelCapability(selectDeclaration)], body = "less"
    |> ExprQualifiedVar("Select")
    |> Some)) with
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
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } ->
            test.fail(
                "curried operation should resolve: " + Ashes.Trait.Show.show(error)
            )

let expectReservedCapability unit =
    (let reserved = CapabilityDecl(name = "Entropy", typeParameters = [], operations = [])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelCapability(reserved)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ReservedCapabilityDeclaration("Entropy")) } -> Unit
            | _ -> test.fail("built-in runtime capability names should stay reserved"))

let expectDuplicateOperation unit =
    (let duplicate =
        CapabilityDecl(name = "Clock", typeParameters = [], operations = [CapabilityOperation(name = "now", signature = None
        |> TypeArrow(TypeUnit)(TypeNamed("Int"))([])
        |> Some), CapabilityOperation(name = "now", signature = None
        |> TypeArrow(TypeUnit)(TypeNamed("Int"))([])
        |> Some)])
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
    (let invalid =
        CapabilityDecl(name = "Clock", typeParameters = [], operations = [CapabilityOperation(name = "now", signature = Some(
            TypeNamed("Int")
        ))])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelCapability(invalid)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(CapabilityOperationRequiresFunction("Clock", "now")) } -> Unit
            | _ -> test.fail("capability operation signatures should be functions"))

let expectDirectCapabilityEffects unit =
    (let clockCall =
        ExprCall(ExprQualifiedVar("Clock")("now"))(ExprTuple([]))(false)(callArgumentsInline)
    in
        let logCall =
            ExprCall(ExprQualifiedVar("Log")("write"))(ExprString("message"))(false)(callArgumentsInline)
        in
            let declarations = [TopLevelCapability(clockDeclaration), TopLevelCapability(logDeclaration)]
            in
                None
                |> ExprLambda("ignored")(clockCall)
                |> expectLambdaCapability("Clock")([TopLevelCapability(clockDeclaration)])
                |> (given (_) ->
                    None
                    |> ExprLambda("ignored")(ExprPerform(clockCall))
                    |> expectLambdaCapability("Clock")([TopLevelCapability(clockDeclaration)]))
                |> (given (_) ->
                    None
                    |> ExprLambda("ignored")(ExprTuple([clockCall, logCall]))
                    |> expectLambdaCapability("Clock")(declarations))
                |> (given (_) ->
                    None
                    |> ExprLambda("ignored")(ExprTuple([clockCall, logCall]))
                    |> expectLambdaCapability("Log")(declarations)))

let expectHigherOrderEffects unit =
    (let higherOrder =
        ExprLambda("operation")(ExprCall(ExprVar("operation"))(ExprTuple([]))(false)(callArgumentsInline))(None)
    in
        match Unit
        |> emptyTypeEnvironment
        |> inferExpression(higherOrder) with
            | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, constraints = _constraints, error = None } ->
                match applySubstitution(substitution)(semanticType) with
                    | SemFunction(SemFunction(SemTuple([]), SemVariable(parameterResultId), Some(parameterRow)), SemVariable(bodyResultId), Some(bodyRow)) ->
                        if parameterResultId == bodyResultId
                        then test.assertEqual(parameterRow)(bodyRow)
                        else test.fail("higher-order call result should flow through")
                    | _ -> test.fail("higher-order effects should remain row-polymorphic")
            | _ -> test.fail("higher-order effect expression should infer"))

let expectInferredLogEffect unit =
    (let lambda =
        ExprLambda(
            "ignored",
            ExprCall(ExprQualifiedVar("Log")("write"))(ExprString("message"))(false)(callArgumentsInline),
            None
        )
    in expectLambdaCapability("Log")([TopLevelCapability(inferredLogDeclaration)])(lambda))

let expectResultMapperEffect unit =
    (let resultType = SemNamed(-1)("Result")([SemInt, SemString])
    in
        let mapperType =
            SemFunction(SemString)(SemBool)(None
            |> SemRow([SemCapability("Clock")([])])
            |> Some)
        in
            let environment =
                Unit
                |> emptyTypeEnvironment
                |> addTypeBinding("input")(TypeScheme(quantified = [], body = resultType, constraints = []))
                |> addTypeBinding("mapper")(TypeScheme(quantified = [], body = mapperType, constraints = []))
            in
                match inferExpression(
                    ExprLambda("ignored")(ExprResultPipe(ExprVar("input"))(ExprVar("mapper")))(None),
                    environment
                ) with
                    | TypeInferenceResult { semanticType = semanticType, substitution = substitution, supply = _supply, constraints = _constraints, error = None } ->
                        match applySubstitution(substitution)(semanticType) with
                            | SemFunction(_argument, _result, Some(row)) ->
                                if rowContainsCapability("Clock")(row)
                                then Unit
                                else test.fail("Result mapper effects should propagate")
                            | _ -> test.fail("Result pipeline wrapper should infer a function")
                    | _ -> test.fail("effectful Result mapper should infer"))

let expectCapabilityEffects unit =
    unit
    |> expectDirectCapabilityEffects
    |> expectHigherOrderEffects
    |> expectInferredLogEffect
    |> expectResultMapperEffect

let expectClosedRowAnnotations unit =
    (let clockCall =
        ExprCall(ExprQualifiedVar("Clock")("now"))(ExprTuple([]))(false)(callArgumentsInline)
    in
        let bindingValue = ExprLambda("ignored")(clockCall)(None)
        in
            let clockAnnotation = TypeArrow(TypeUnit)(TypeNamed("Int"))([("Clock", [])])(None)
            in
                let acceptedBinding =
                    LetBindingSyntax(name = "readClock", value = bindingValue, sugarParameters = [], typeAnnotation = Some(
                        clockAnnotation
                    ), requirements = [])
                in
                    let acceptedChecked =
                        match inferProgram(
                            ProgramSyntax(items = [TopLevelCapability(clockDeclaration), TopLevelLet(
                                acceptedBinding,
                                false
                            )], body = Some(ExprVar("readClock")))
                        ) with
                            | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
                                match applySubstitution(substitution)(semanticType) with
                                    | SemFunction(SemTuple([]), SemInt, Some(row)) ->
                                        if rowContainsCapability("Clock")(row)
                                        then
                                            if rowIsOpen(row)
                                            then
                                                test.fail(
                                                    "written closed capability annotation should not remain open"
                                                )
                                            else Unit
                                        else test.fail("closed annotation should retain Clock")
                                    | _ -> test.fail("closed capability annotation should retain its function type")
                            | _ -> test.fail("matching closed capability annotation should infer")
                    in
                        let logAnnotation = TypeArrow(TypeUnit)(TypeNamed("Int"))([("Log", [])])(None)
                        in
                            let rejectedBinding =
                                LetBindingSyntax(name = "readClock", value = bindingValue, sugarParameters = [], typeAnnotation = Some(
                                    logAnnotation
                                ), requirements = [])
                            in
                                match inferProgram(
                                    ProgramSyntax(items = [TopLevelCapability(
                                        clockDeclaration
                                    ), TopLevelCapability(logDeclaration), TopLevelLet(
                                        rejectedBinding,
                                        false
                                    )], body = None)
                                ) with
                                    | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(_left, _right)))) } -> Unit
                                    | _ -> test.fail("closed capability annotations should reject undeclared effects"))

let expectInvalidPerform unit =
    (let invalidChecked =
        match Unit
        |> emptyTypeEnvironment
        |> inferExpression(ExprPerform(ExprInt(42))) with
            | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(PerformRequiresCapabilityOperation) } -> Unit
            | _ -> test.fail("perform should require a capability operation call")
    in
        let missingCall =
            callArgumentsInline
            |> ExprCall(ExprQualifiedVar("Clock")("missing"))(ExprTuple([]))(false)
            |> ExprPerform
        in
            match inferProgram(
                ProgramSyntax(items = [TopLevelCapability(clockDeclaration)], body = Some(missingCall))
            ) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(UnknownCapabilityOperation("Clock", "missing"))) } -> Unit
                | _ -> test.fail("perform should reject unknown capability operations"))

let expectOperationValueRules unit =
    (let unsignedChecked =
        match inferProgram(ProgramSyntax(items = [TopLevelCapability(inferredLogDeclaration)], body = "write"
        |> ExprQualifiedVar("Log")
        |> Some)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(UnsignedCapabilityOperationRequiresSignature("Log", "write"))) } -> Unit
            | _ -> test.fail("unsigned operations should not be first-class values")
    in
        let missingCall =
            ExprCall(ExprQualifiedVar("Clock")("missing"))(ExprTuple([]))(false)(callArgumentsInline)
        in
            match inferProgram(
                ProgramSyntax(items = [TopLevelCapability(clockDeclaration)], body = Some(missingCall))
            ) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(UnknownCapabilityOperation("Clock", "missing"))) } -> Unit
                | _ -> test.fail("implicit calls should reject unknown capability operations"))

let handlerClockCall =
    ExprCall(ExprQualifiedVar("Clock")("now"))(ExprTuple([]))(false)(callArgumentsInline)

let resumeWith value = ExprCall(ExprVar("resume"))(value)(false)(callArgumentsInline)

let expectHandlerDischarges expectedResult dischargedCapability remainingCapability declarations handler =
    (let wrapper = ExprLambda("ignored")(handler)(None)
    in
        match inferProgram(ProgramSyntax(items = declarations, body = Some(wrapper))) with
            | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
                match applySubstitution(substitution)(semanticType) with
                    | SemFunction(_argument, resultType, Some(row)) ->
                        if formatSemanticType(resultType) == expectedResult
                        then
                            if rowContainsCapability(dischargedCapability)(row)
                            then test.fail("handler should discharge " + dischargedCapability)
                            else
                                match remainingCapability with
                                    | None -> Unit
                                    | Some(name) ->
                                        if rowContainsCapability(name)(row)
                                        then Unit
                                        else test.fail("handler arm effect should propagate " + name)
                        else test.fail("handler inferred unexpected result " + formatSemanticType(resultType))
                    | _ -> test.fail("handler wrapper should infer a function")
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } ->
                test.fail(
                    "handler should infer: " + Ashes.Trait.Show.show(error)
                ))

let expectHandlerInference unit =
    (let clockArm = (Some("Clock"), "now", [PatternWildcard], resumeWith(ExprInt(42)))
    in
        let basicChecked =
            [clockArm]
            |> ExprHandle(handlerClockCall)
            |> expectHandlerDischarges("Int")("Clock")(None)([TopLevelCapability(clockDeclaration)])
        in
            let returnArm = (None, "return", [PatternVar("value")], ExprString("done"))
            in
                let returnChecked =
                    [clockArm, returnArm]
                    |> ExprHandle(handlerClockCall)
                    |> expectHandlerDischarges("Str")("Clock")(None)([TopLevelCapability(clockDeclaration)])
                in
                    let loggingArmBody =
                        ExprLet(
                            "ignored",
                            ExprCall(
                                ExprQualifiedVar("Log")("write"),
                                ExprString("handled"),
                                false,
                                callArgumentsInline
                            ),
                            resumeWith(ExprInt(42)),
                            [],
                            None,
                            []
                        )
                    in
                        let loggingArm = (Some("Clock"), "now", [PatternWildcard], loggingArmBody)
                        in
                            let armEffectChecked =
                                [loggingArm]
                                |> ExprHandle(handlerClockCall)
                                |> expectHandlerDischarges(
                                    "Int",
                                    "Clock",
                                    Some("Log"),
                                    [TopLevelCapability(clockDeclaration), TopLevelCapability(logDeclaration)]
                                )
                            in
                                let stateGet =
                                    ExprCall(
                                        ExprQualifiedVar("State")("get"),
                                        ExprTuple([]),
                                        false,
                                        callArgumentsInline
                                    )
                                in
                                    let getArm = (Some("State"), "get", [PatternWildcard], resumeWith(ExprInt(7)))
                                    in
                                        let setArm =
                                            (Some("State"), "set", [PatternVar("value")], resumeWith(
                                                ExprTuple([])
                                            ))
                                        in
                                            let stateChecked =
                                                [getArm, setArm]
                                                |> ExprHandle(stateGet)
                                                |> expectHandlerDischarges(
                                                    "Int",
                                                    "State",
                                                    None,
                                                    [TopLevelCapability(stateDeclaration)]
                                                )
                                            in Unit)

let expectInvalidHandlers unit =
    (let clockArm = (Some("Clock"), "now", [PatternWildcard], resumeWith(ExprInt(42)))
    in
        let duplicateChecked =
            match inferProgram(ProgramSyntax(items = [TopLevelCapability(clockDeclaration)], body = [clockArm, clockArm]
            |> ExprHandle(handlerClockCall)
            |> Some)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InvalidHandler(_message))) } -> Unit
                | _ -> test.fail("duplicate handler arms should be rejected")
        in
            let getArm = (Some("State"), "get", [PatternWildcard], resumeWith(ExprInt(7)))
            in
                let incompleteChecked =
                    match inferProgram(ProgramSyntax(items = [TopLevelCapability(stateDeclaration)], body = [getArm]
                    |> ExprHandle(ExprCall(ExprQualifiedVar("State")("get"))(ExprTuple([]))(false)(callArgumentsInline))
                    |> Some)) with
                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InvalidHandler(_message))) } -> Unit
                        | _ -> test.fail("handlers should cover every capability operation")
                in
                    let badPatternArm = (Some("Clock"), "now", [PatternInt(1)], resumeWith(ExprInt(42)))
                    in
                        let patternChecked =
                            match inferProgram(ProgramSyntax(items = [TopLevelCapability(
                                clockDeclaration
                            )], body = [badPatternArm]
                            |> ExprHandle(handlerClockCall)
                            |> Some)) with
                                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(_left, _right)))) } -> Unit
                                | _ -> test.fail("handler arm patterns should match operation parameters")
                        in
                            let badResumeArm =
                                (Some("Clock"), "now", [PatternWildcard], resumeWith(
                                    ExprString("wrong")
                                ))
                            in
                                let resumeChecked =
                                    match inferProgram(ProgramSyntax(items = [TopLevelCapability(
                                        clockDeclaration
                                    )], body = [badResumeArm]
                                    |> ExprHandle(handlerClockCall)
                                    |> Some)) with
                                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(_left, _right)))) } -> Unit
                                        | _ -> test.fail("resume arguments should match operation results")
                                in
                                    let returnOnly = (None, "return", [PatternVar("value")], ExprVar("value"))
                                    in
                                        match Unit
                                        |> emptyTypeEnvironment
                                        |> inferExpression(ExprHandle(ExprInt(1))([returnOnly])) with
                                            | TypeInferenceResult { semanticType = _semanticType, substitution = _substitution, supply = _supply, constraints = _constraints, error = Some(InvalidHandler(_message)) } -> Unit
                                            | _ -> test.fail("handlers should require an operation arm"))

let clockProvider =
    ProvideDecl(capabilityName = "Clock", typeArguments = [], bindings = [ProvideBinding(operationName = "now", implementation = ExprLambda(
        "ignored",
        ExprInt(1000),
        None
    ))])

let stateIntProvider =
    ProvideDecl(capabilityName = "State", typeArguments = [TypeNamed(
        "Int"
    )], bindings = [ProvideBinding(operationName = "get", implementation = ExprLambda(
        "ignored",
        ExprInt(0),
        None
    )), ProvideBinding(operationName = "set", implementation = ExprLambda(
        "value",
        ExprTuple([]),
        Some(TypeNamed("Int"))
    ))])

let expectProviderInference unit =
    (let clockChecked =
        match inferProgram(
            ProgramSyntax(items = [TopLevelCapability(clockDeclaration), TopLevelProvide(clockProvider)], body = None)
        ) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = environment, error = None } ->
                match resolveCapabilityProvider(SemCapability("Clock")([]))(environment) with
                    | Some(_) -> Unit
                    | None -> test.fail("Clock provider should be registered")
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } ->
                test.fail(
                    "Clock provider should infer: " + Ashes.Trait.Show.show(error)
                )
    in
        let stateChecked =
            match inferProgram(
                ProgramSyntax(items = [TopLevelCapability(stateDeclaration), TopLevelProvide(
                    stateIntProvider
                )], body = None)
            ) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = environment, error = None } ->
                    match resolveCapabilityProvider(SemCapability("State")([SemInt]))(environment) with
                        | Some(_) -> Unit
                        | None -> test.fail("State(Int) provider should be registered")
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } ->
                    test.fail(
                        "State(Int) provider should infer: " + Ashes.Trait.Show.show(error)
                    )
        in Unit)

let expectProviderSatisfaction unit =
    (let clockWrapper = ExprLambda("ignored")(handlerClockCall)(None)
    in
        let clockChecked =
            match inferProgram(
                ProgramSyntax(items = [TopLevelCapability(
                    clockDeclaration
                ), TopLevelProvide(clockProvider)], body = Some(clockWrapper))
            ) with
                | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
                    match applySubstitution(substitution)(semanticType) with
                        | SemFunction(_argument, SemInt, Some(row)) ->
                            if rowContainsCapability("Clock")(row)
                            then test.fail("a concrete Clock provider should satisfy Clock calls")
                            else Unit
                        | _ -> test.fail("a provider-backed Clock call should retain its result type")
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } ->
                    test.fail(
                        "a provider-backed Clock call should infer: " + Ashes.Trait.Show.show(error)
                    )
        in
            let stateSet =
                ExprCall(ExprQualifiedVar("State")("set"))(ExprInt(42))(false)(callArgumentsInline)
            in
                let stateWrapper = ExprLambda("ignored")(stateSet)(None)
                in
                    let stateChecked =
                        match inferProgram(
                            ProgramSyntax(items = [TopLevelCapability(stateDeclaration), TopLevelProvide(
                                stateIntProvider
                            )], body = Some(stateWrapper))
                        ) with
                            | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
                                match applySubstitution(substitution)(semanticType) with
                                    | SemFunction(_argument, SemTuple([]), Some(row)) ->
                                        if rowContainsCapability("State")(row)
                                        then test.fail("a concrete State(Int) provider should satisfy State(Int) calls")
                                        else Unit
                                    | _ -> test.fail("a provider-backed State.set call should retain its result type")
                            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } ->
                                test.fail(
                                    "a provider-backed State(Int) call should infer: " + Ashes.Trait.Show.show(error)
                                )
                    in
                        let abstractSet =
                            ExprLambda(
                                "value",
                                ExprCall(
                                    ExprQualifiedVar("State")("set"),
                                    ExprVar("value"),
                                    false,
                                    callArgumentsInline
                                ),
                                None
                            )
                        in
                            match inferProgram(
                                ProgramSyntax(items = [TopLevelCapability(stateDeclaration), TopLevelProvide(
                                    stateIntProvider
                                )], body = Some(abstractSet))
                            ) with
                                | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
                                    match applySubstitution(substitution)(semanticType) with
                                        | SemFunction(_argument, SemTuple([]), Some(row)) ->
                                            if rowContainsCapability("State")(row)
                                            then Unit
                                            else
                                                test.fail(
                                                    "an abstract State requirement should not select a concrete provider"
                                                )
                                        | _ -> test.fail("an abstract State call should retain its function type")
                                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } ->
                                    test.fail(
                                        "an abstract State call should infer: " + Ashes.Trait.Show.show(error)
                                    ))

let expectProviderHandlerAmbiguity unit =
    (let clockArm = (Some("Clock"), "now", [PatternWildcard], resumeWith(ExprInt(42)))
    in
        let handler = ExprHandle(handlerClockCall)([clockArm])
        in
            match inferProgram(
                ProgramSyntax(items = [TopLevelCapability(
                    clockDeclaration
                ), TopLevelProvide(clockProvider)], body = Some(handler))
            ) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(AmbiguousCapabilitySatisfaction("Clock"))) } -> Unit
                | _ -> test.fail("a provider and handler for the same concrete capability should be ambiguous"))

let rejectDuplicateProvider unit =
    match inferProgram(
        ProgramSyntax(items = [TopLevelCapability(
            clockDeclaration
        ), TopLevelProvide(clockProvider), TopLevelProvide(clockProvider)], body = None)
    ) with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(DuplicateCapabilityProvider(SemCapability("Clock", []))) } -> Unit
        | _ -> test.fail("duplicate providers should be rejected")

let rejectMissingProviderOperation unit =
    (let provider =
        ProvideDecl(capabilityName = "State", typeArguments = [TypeNamed(
            "Int"
        )], bindings = [ProvideBinding(operationName = "get", implementation = ExprLambda(
            "ignored",
            ExprInt(0),
            None
        ))])
    in
        match inferProgram(
            ProgramSyntax(items = [TopLevelCapability(stateDeclaration), TopLevelProvide(provider)], body = None)
        ) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(MissingProviderOperation("State", "set")) } -> Unit
            | _ -> test.fail("providers should implement every operation"))

let rejectUnknownProviderOperation unit =
    (let provider =
        ProvideDecl(capabilityName = "Clock", typeArguments = [], bindings = [ProvideBinding(operationName = "missing", implementation = ExprLambda(
            "ignored",
            ExprInt(0),
            None
        ))])
    in
        match inferProgram(
            ProgramSyntax(items = [TopLevelCapability(clockDeclaration), TopLevelProvide(provider)], body = None)
        ) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(UnknownProviderOperation("Clock", "missing")) } -> Unit
            | _ -> test.fail("providers should reject unknown operations"))

let rejectDuplicateProviderOperation unit =
    (let provider =
        ProvideDecl(capabilityName = "Clock", typeArguments = [], bindings = [ProvideBinding(operationName = "now", implementation = ExprLambda(
            "ignored",
            ExprInt(0),
            None
        )), ProvideBinding(operationName = "now", implementation = ExprLambda(
            "ignored",
            ExprInt(1),
            None
        ))])
    in
        match inferProgram(
            ProgramSyntax(items = [TopLevelCapability(clockDeclaration), TopLevelProvide(provider)], body = None)
        ) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(DuplicateProviderOperation("Clock", "now")) } -> Unit
            | _ -> test.fail("providers should reject duplicate operations"))

let rejectProviderCapabilityArity unit =
    match inferProgram(
        ProgramSyntax(items = [TopLevelCapability(stateDeclaration), TopLevelProvide(
            ProvideDecl(capabilityName = "State", typeArguments = [], bindings = [])
        )], body = None)
    ) with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProviderCapabilityArityMismatch("State", 1, 0)) } -> Unit
        | _ -> test.fail("provider capability arity should be checked")

let rejectProviderImplementationType unit =
    (let provider =
        ProvideDecl(capabilityName = "Clock", typeArguments = [], bindings = [ProvideBinding(operationName = "now", implementation = ExprLambda(
            "ignored",
            ExprString("wrong"),
            None
        ))])
    in
        match inferProgram(
            ProgramSyntax(items = [TopLevelCapability(clockDeclaration), TopLevelProvide(provider)], body = None)
        ) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(_left, _right)))) } -> Unit
            | _ -> test.fail("provider implementations should match operation signatures"))

let expectInvalidProviders unit =
    unit
    |> rejectDuplicateProvider
    |> rejectMissingProviderOperation
    |> rejectUnknownProviderOperation
    |> rejectDuplicateProviderOperation
    |> rejectProviderCapabilityArity
    |> rejectProviderImplementationType

let runCapabilityInferenceTests unit =
    unit
    |> expectClockOperation
    |> expectParameterizedOperation
    |> expectDuplicateCapability
    |> expectInnermostArrow
    |> expectReservedCapability
    |> expectDuplicateOperation
    |> expectParameterizedSignature
    |> expectFunctionSignature
    |> expectCapabilityEffects
    |> expectClosedRowAnnotations
    |> expectInvalidPerform
    |> expectOperationValueRules
    |> expectHandlerInference
    |> expectInvalidHandlers
    |> expectProviderInference
    |> expectProviderSatisfaction
    |> expectProviderHandlerAmbiguity
    |> expectInvalidProviders
    |> (given (_) -> Ashes.IO.print("all self-hosted capability provider inference tests passed"))
