import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ProgramInference
let clockDeclaration = CapabilityDecl(name = "Clock", typeParameters = [], operations = [CapabilityOperation(name = "now", signature = Some(TypeArrow(TypeUnit)(TypeNamed("Int"))([])(None)))])

let stateDeclaration = CapabilityDecl(name = "State", typeParameters = [TypeParameter(name = "a")], operations = [CapabilityOperation(name = "get", signature = Some(TypeArrow(TypeUnit)(TypeNamed("a"))([])(None))), CapabilityOperation(name = "set", signature = Some(TypeArrow(TypeNamed("a"))(TypeUnit)([])(None)))])

let selectDeclaration = CapabilityDecl(name = "Select", typeParameters = [TypeParameter(name = "a")], operations = [CapabilityOperation(name = "less", signature = Some(TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None))([])(None)))])

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
            let actual = formatSemanticType(applySubstitution(substitution)(semanticType))
            in test.assertEqual("?1 -> ?1 -> Bool needs {Select(?1)}")(actual)
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
                                in Ashes.IO.print("all self-hosted capability declaration inference tests passed"))
