import CoreOrderedInferenceTests.readShippedSources
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.NodeIdentity
import AshesCompiler.Semantics.NodeTypeTable
import AshesCompiler.Semantics.ProjectInference
import AshesCompiler.Semantics.ShippedModuleStitching.stitchWithShippedModules
import AshesCompiler.Semantics.StandardTraits.withIntrinsicBuiltinSignatures
import AshesCompiler.Semantics.TypeInference.emptyTypeEnvironmentForPackage
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.CoreLowering.Emit.initialState
import AshesCompiler.Semantics.CoreLowering.LoweringState.withNodeTypes
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedNestedOperatorGenericScheme
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.translateRecordedType
import AshesCompiler.Semantics.CoreLowering.LoweringState.withStateActiveTypeParameters
import AshesCompiler.Semantics.StandardTraits.standardTraitEnvironment
export (
    value runRecordedInferenceCoverageTests,
)

let recursive bindingType name (bindings: List((Str, TypeScheme))) =
    match bindings with
        | [] -> None
        | (candidate, scheme) :: rest ->
            if candidate == name
            then Some(scheme.body)
            else bindingType(name)(rest)

let checkArm (table: NodeTypeTable) found _node _span expression =
    match expression with
        | ExprHandle(_body, (_capability, "examine", _patterns, arm) :: _) ->
            let bindings =
                nodeBindingsAt(nodeIdentityOf(arm))(table)
            in
                match (bindingType("Ready")(bindings), bindingType("text")(bindings), bindingType("resume")(bindings)) with
                    | (None, Some(SemString), Some(SemFunction(SemString, SemString, _row))) -> true
                    | _ -> Ashes.IO.panic("handler arm must bind its tuple variable and continuation, excluding the nullary constructor")
        | _ -> found

let checkHandlerBindings unit =
    (let source = "type Flag = | Ready\ncapability Inspect = | examine : (Flag, Str) -> Str\nhandle perform Inspect.examine((Ready, \"value\")) with | Inspect.examine((Ready, text)) -> resume(text) | return(result) -> result"
    in
        match parseProgram(source) with
            | ProgramParseResult { program = parsed, diagnostics = [] } ->
                match identifyProgram(0)(parsed) with
                    | (program, count) ->
                        match inferProgram(program) with
                            | ProgramInferenceResult { nodeTypes = table, error = None } ->
                                if table.typeCount == count && foldProgramNodes(checkArm(table))(false)(program)
                                then Unit
                                else Ashes.IO.panic("handler expression coverage incomplete")
                            | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("handler inference failed: " + Ashes.Trait.Show.show(error))
            | _ -> Ashes.IO.panic("handler inference fixture did not parse"))

let checkComplement (table: NodeTypeTable) count node _span expression =
    match expression with
        | ExprBitwiseNot(operand) ->
            match (nodeOperatorAt(node)(table), nodeTypeAt(nodeIdentityOf(operand))(table)) with
                | (Some(NodeOperator { traitName = "BitwiseNot", instance = PrimitiveInstance, operandType = recorded }), Some(actual)) ->
                    if actual == recorded
                    then count + 1
                    else Ashes.IO.panic("complement operator recorded a different operand type")
                | _ -> Ashes.IO.panic("integer complement must record a primitive instance")
        | _ -> count

let checkPrimitiveComplements unit =
    match parseProgram("(~1, ~1u8, ~1u16, ~1u32, ~1u64)") with
        | ProgramParseResult { program = parsed, diagnostics = [] } ->
            match identifyProgram(0)(parsed) with
                | (program, count) ->
                    match inferProgramFromPackage("standalone")(standardTraitEnvironment(Unit))(program) with
                        | ProgramInferenceResult { nodeTypes = table, error = None } ->
                            if table.typeCount == count && foldProgramNodes(checkComplement(table))(0)(program) == 5
                            then Unit
                            else Ashes.IO.panic("complement expression coverage incomplete")
                        | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("complement inference failed: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic("integer complement fixture did not parse")

let checkShadowingLet state found node _span expression =
    match expression with
        | ExprLet("value", rhs, _body, _parameters, _annotation, _requirements) ->
            match recordedNestedOperatorGenericScheme(node)("value")(nodeIdentityOf(rhs))(state) with
                | Some(TypeScheme { quantified = _ :: _, constraints = TraitConstraint { traitName = "Add" } :: _ }) -> true
                | _ -> Ashes.IO.panic("the arm's Int binding hid the inner value's quantified Add scheme")
        | _ -> found

let checkArmBodyShadowing unit =
    match parseProgram("match 7 with | value -> let value = given x -> x + x in value(3)") with
        | ProgramParseResult { program = parsed, diagnostics = [] } ->
            match identifyProgram(0)(parsed) with
                | (program, _count) ->
                    match inferProgramFromPackage("standalone")(standardTraitEnvironment(Unit))(program) with
                        | ProgramInferenceResult { nodeTypes = table, error = None } ->
                            let state =
                                Unit
                                |> initialState
                                |> withNodeTypes(table)
                            in
                                if foldProgramNodes(checkShadowingLet(state))(false)(program)
                                then Unit
                                else Ashes.IO.panic("missing shadowing let")
                        | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("shadowing inference failed: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic("shadowing inference fixture did not parse")

let checkRecordInstantiation (table: NodeTypeTable) node count =
    match (nodeTypeAt(node)(table), nodeInstantiationAt(node)(table)) with
        | (Some(SemNamed(_id, "Box", element :: [])), Some(NodeInstantiation { arguments = (_variable, argument) :: [] })) ->
            if element == argument
            then count + 1
            else Ashes.IO.panic("record constructor argument and result layout disagree")
        | _ -> Ashes.IO.panic("record construction or update lost its own instantiation")

let checkRecordNode (table: NodeTypeTable) count node _span expression =
    match expression with
        | ExprRecord("Box", _fields, _) -> checkRecordInstantiation(table)(node)(count)
        | ExprRecordUpdate(_target, _fields) -> checkRecordInstantiation(table)(node)(count)
        | _ -> count

let checkRecordInstantiations unit =
    (let source = "type Box(a) = | value: a\nlet first = Box(value = 1)\nlet second = Box(value = \"two\")\n((first with value = 3), (second with value = \"four\"))"
    in
        match parseProgram(source) with
            | ProgramParseResult { program = parsed, diagnostics = [] } ->
                match identifyProgram(0)(parsed) with
                    | (program, count) ->
                        match inferProgram(program) with
                            | ProgramInferenceResult { nodeTypes = table, error = None, semanticType = SemTuple(SemNamed(_, "Box", SemInt :: []) :: SemNamed(_, "Box", SemString :: []) :: []) } ->
                                if table.typeCount == count && foldProgramNodes(checkRecordNode(table))(0)(program) == 4
                                then Unit
                                else Ashes.IO.panic("record constructor or update lacks a numbered type")
                            | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("record inference failed: " + Ashes.Trait.Show.show(error))
                            | _ -> Ashes.IO.panic("distinct record instantiations lost their result types")
            | _ -> Ashes.IO.panic("record instantiation fixture did not parse"))

let checkTypeTranslation unit =
    (let state = initialState(Unit)
    in
        let leaves = [SemInt, SemUInt(8), SemFloat, SemBigInt, SemString, SemRune, SemBytes, SemBool, SemNever, SemVariable(73), SemPointer(SemOpaque("Handle"))]
        in
            let source =
                Some(SemVariable(75))
                |> SemRow([SemCapability("Inspect")([SemVariable(74)])])
                |> Some
                |> SemFunction(SemNamed(91)("Maybe")([SemList(SemTuple(leaves))]))(SemTuple([]))
            in
                match translateRecordedType(state)(source) with
                    | Ok(SemFunction(SemNamed(0, "Maybe", SemList(SemTuple(translated)) :: []), SemTuple([]), None)) ->
                        if translated == leaves
                        then Unit
                        else Ashes.IO.panic("recorded aggregate translation changed a leaf or closed an unconstrained variable")
                    | _ -> Ashes.IO.panic("recorded type translation lost a named argument or kept a lowering capability row"))

let checkRigidScope unit =
    (let state = initialState(Unit)
    in
        match ("a"
        |> SemParameter(1)
        |> translateRecordedType(state), "a"
        |> SemParameter(1)
        |> translateRecordedType(withStateActiveTypeParameters([("a", SemVariable(81))])(state)), []
        |> SemCapability("Inspect")
        |> translateRecordedType(state), None
        |> SemRow([])
        |> translateRecordedType(state)) with
            | (Error(_unscoped), Ok(SemVariable(81)), Error(_capability), Error(_row)) -> Unit
            | _ -> Ashes.IO.panic("recorded type translation did not respect the rigid parameter scope or standalone row boundary"))

let checkDerivedTree unit =
    (let path = "tests/trait_deriving.ash"
    in
        match (Ashes.IO.Directory.entries("lib/Ashes"), Ashes.IO.File.readText(path)) with
            | (Ok(names), Ok(source)) ->
                match names
                |> readShippedSources
                |> stitchWithShippedModules("regression")(path)(source) with
                    | Ok(project) ->
                        let identified = identifyStitchedProject(project)
                        in
                            match identified.program.error with
                                | Some(error) -> Ashes.IO.panic("recursive deriving expansion failed before numbering: " + Ashes.Trait.Show.show(error))
                                | None ->
                                    let environment =
                                        "standalone"
                                        |> emptyTypeEnvironmentForPackage
                                        |> withIntrinsicBuiltinSignatures
                                    in
                                        match inferIdentifiedStitchedProjectFrom(environment)(project)(identified) with
                                            | ProgramInferenceResult { nodeTypes = table, error = None } ->
                                                if identified.program.nodeCount > 0 && table.typeCount == identified.program.nodeCount
                                                then Unit
                                                else Ashes.IO.panic("derived implementations contain untyped numbered expressions")
                                            | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("recursive deriving inference failed: " + Ashes.Trait.Show.show(error))
                    | Error(error) -> Ashes.IO.panic("recursive deriving fixture failed to stitch: " + Ashes.Trait.Show.show(error))
            | (Error(message), _) -> Ashes.IO.panic(message)
            | (_, Error(message)) -> Ashes.IO.panic(message))

let runRecordedInferenceCoverageTests unit =
    unit
    |> checkHandlerBindings
    |> checkPrimitiveComplements
    |> checkArmBodyShadowing
    |> checkRecordInstantiations
    |> checkTypeTranslation
    |> checkRigidScope
    |> checkDerivedTree
