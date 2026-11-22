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
import AshesCompiler.Semantics.CoreLowering.measureCoreProgramLowering
import AshesCompiler.Semantics.CoreLowering.measureRawCoreProgramLowering
import AshesCompiler.Semantics.CoreLowering.LoweringState.withNodeTypes
import AshesCompiler.Semantics.CoreLowering.LoweringState.withStateNodeInstantiation
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedNestedOperatorGenericScheme
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.translateRecordedType
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.recordedConcreteCallType
import AshesCompiler.Semantics.CoreLowering.Owners.stripExprAt
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
                        match identifyStitchedProject(project) with
                            | IdentifiedStitchedProject { program = numbered } as identified ->
                                match numbered.error with
                                    | Some(error) -> Ashes.IO.panic("recursive deriving expansion failed before numbering: " + Ashes.Trait.Show.show(error))
                                    | None ->
                                        let environment =
                                            "standalone"
                                            |> emptyTypeEnvironmentForPackage
                                            |> withIntrinsicBuiltinSignatures
                                        in
                                            match inferIdentifiedStitchedProjectFrom(environment)(project)(identified) with
                                                | ProgramInferenceResult { nodeTypes = table, error = None } ->
                                                    if numbered.nodeCount > 0 && table.typeCount == numbered.nodeCount
                                                    then Unit
                                                    else Ashes.IO.panic("derived implementations contain untyped numbered expressions")
                                                | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("recursive deriving inference failed: " + Ashes.Trait.Show.show(error))
                    | Error(error) -> Ashes.IO.panic("recursive deriving fixture failed to stitch: " + Ashes.Trait.Show.show(error))
            | (Error(message), _) -> Ashes.IO.panic(message)
            | (_, Error(message)) -> Ashes.IO.panic(message))

let checkGuardScope table found _node _span expression =
    match expression with
        | ExprMatch(_scrutinee, (_pattern, body, Some(guard)) :: _, _) ->
            match (table
            |> nodeBindingsAt(nodeIdentityOf(guard))
            |> bindingType("word"), table
            |> nodeBindingsAt(nodeIdentityOf(body))
            |> bindingType("word")) with
                | (Some(SemString), Some(SemString)) -> true
                | _ -> Ashes.IO.panic("or-pattern binder should have the same type in its guard and its body")
        | _ -> found

let checkGuardedOrPattern unit =
    (let source = "type Choice = | First(Str) | Second(Str)\nmatch First(\"a\") with | First(word) | Second(word) when true -> word | _ -> \"fallback\""
    in
        match parseProgram(source) with
            | ProgramParseResult { program = parsed, diagnostics = [] } ->
                match identifyProgram(0)(parsed) with
                    | (program, _count) ->
                        match inferProgram(program) with
                            | ProgramInferenceResult { nodeTypes = table, error = None } ->
                                if foldProgramNodes(checkGuardScope(table))(false)(program)
                                then Unit
                                else Ashes.IO.panic("missing guarded or-pattern")
                            | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("guard inference failed: " + Ashes.Trait.Show.show(error))
            | _ -> Ashes.IO.panic("guarded or-pattern fixture did not parse"))

// Use inferred tables from two concrete references to one generic source. The
// copy returns an aggregate, so confusing these layouts also changes ownership.
let copySourceNode table found node _span _expression =
    match nodeDeclarationAt(node)(table) with
        | Some(("twice", _scheme)) -> node
        | _ -> found

let checkCopySite state source count _node _span expression =
    match expression with
        | ExprCall(callee, argument, _space, _layout) ->
            match stripExprAt(callee) with
                | ExprVar("twice") ->
                    let reference = nodeIdentityOf(callee)
                    in
                        match (stripExprAt(argument), recordedConcreteCallType(reference)(source)(1)([argument])(state)) with
                            | (ExprInt(_), Some(SemFunction(SemInt, SemList(SemInt), None))) ->
                                if recordedConcreteCallType(reference)(source)(1)([ExprInt(0)])(state) == None && recordedConcreteCallType(reference)(reference)(1)([argument])(state) == None
                                then count + 1
                                else Ashes.IO.panic("concrete copy accepted a synthesized argument or unrelated definition")
                            | (ExprFloat(_, _), Some(SemFunction(SemFloat, SemList(SemFloat), None))) -> count + 1
                            | _ -> Ashes.IO.panic("recorded concrete copy lost a call's own parameters or aggregate result")
                | _ -> count
        | _ -> count

let checkCopyLoweringPaths table program =
    (let clock =
        given (_unit) -> 0
    in
        let typed = measureCoreProgramLowering(clock)(Some(table))("copies.ash")("")(program)
        in
            let legacy = measureRawCoreProgramLowering(clock)(table)("copies.ash")("")(program)
            in
                let firstTyped = typed.first
                in
                    let firstLegacy = legacy.first
                    in
                        if firstTyped.error == None && firstLegacy.error == None && firstTyped.recordedCopyCalls >= 2 && firstTyped.inferredCopyCalls == 0 && firstLegacy.recordedCopyCalls == 0 && firstLegacy.inferredCopyCalls >= 2
                        then Unit
                        else Ashes.IO.panic("concrete copy lowering paths: typed recorded=" + Ashes.Text.fromInt(firstTyped.recordedCopyCalls) + ", typed inferred=" + Ashes.Text.fromInt(firstTyped.inferredCopyCalls) + ", legacy inferred=" + Ashes.Text.fromInt(firstLegacy.inferredCopyCalls)))

let checkConcreteCopyFacts unit =
    match parseProgram("let twice x = [x + x, x]\n(twice(1), twice(1.5))") with
        | ProgramParseResult { program = parsed, diagnostics = [] } ->
            match identifyProgram(0)(parsed) with
                | (program, _count) ->
                    match inferProgramFromPackage("standalone")(standardTraitEnvironment(Unit))(program) with
                        | ProgramInferenceResult { nodeTypes = table, error = None } ->
                            let source =
                                foldProgramNodes(copySourceNode(table))(-1)(program)
                            in
                                let state =
                                    Unit
                                    |> initialState
                                    |> withNodeTypes(table)
                                in
                                    if source >= 0 && foldProgramNodes(checkCopySite(state)(source))(0)(program) == 2
                                    then checkCopyLoweringPaths(table)(program)
                                    else Ashes.IO.panic("missing concrete copy references")
                        | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("copy inference failed: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic("copy fixture did not parse")

// Raw-source lowering, which reads no node table, still gives an operator-generic `let` below top
// level a copy per concrete call: each case uses the binding at Int and at Float, and failed with
// an Int vs Float mismatch while only top-level bindings were known by name.
let checkRawNestedLetCopies (name: Str) (source: Str) =
    match parseProgram(source) with
        | ProgramParseResult { program = parsed, diagnostics = [] } ->
            match identifyProgram(0)(parsed) with
                | (program, _count) ->
                    match inferProgramFromPackage("standalone")(standardTraitEnvironment(Unit))(program) with
                        | ProgramInferenceResult { nodeTypes = table, error = None } ->
                            let clock =
                                given (_unit) -> 0
                            in
                                let legacy = measureRawCoreProgramLowering(clock)(table)("nested.ash")("")(program)
                                in
                                    let first = legacy.first
                                    in
                                        if first.error == None && first.inferredCopyCalls >= 1
                                        then Unit
                                        else Ashes.IO.panic(name + ": raw nested let lowering, error=" + Ashes.Trait.Show.show(first.error != None) + ", inferred copies=" + Ashes.Text.fromInt(first.inferredCopyCalls))
                        | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic(name + ": inference failed: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic(name + ": fixture did not parse")

let checkRawNestedLets unit =
    (let _ = checkRawNestedLetCopies("trailing value")("let both =\n    (let double x = x + x\n    in (double(5), double(1.25)))\nboth")
    in
        let _ = checkRawNestedLetCopies("function body reading its parameter")("let describe n =\n    (let double x = x + x\n    in (double(n), double(1.75)))\ndescribe(6)")
        in checkRawNestedLetCopies("captured by an inner lambda")("let describe n =\n    (let double x = x + x\n    in\n        let both = given (unit) -> (double(n), double(1.75))\n        in both(Unit))\ndescribe(6)"))

// Inside a specialized wrapper the use can still carry that wrapper's
// variable id. Read it through the current copy; do not guess it when open.
let recordedCopyInFrame sourceRow useRow rowArguments enclosing =
    (let formal = SemVariable(7)
    in
        let actual = SemVariable(40)
        in
            let source = SemFunction(formal)(SemList(formal))(sourceRow)
            in
                let called = SemFunction(actual)(SemList(actual))(useRow)
                in
                    let reference = NodeInstantiation(arguments = (7, actual) :: rowArguments, requirements = [], dictionaries = [], evidenceDemands = [])
                    in
                        let table =
                            emptyNodeTypeTable with types = Ashes.Collection.Map.empty |> Ashes.Collection.Map.setInt(1)(source) |> Ashes.Collection.Map.setInt(2)(called) |> Ashes.Collection.Map.setInt(3)(actual), instantiations = Ashes.Collection.Map.setInt(2)(reference)(Ashes.Collection.Map.empty)
                        in
                            Unit
                            |> initialState
                            |> withNodeTypes(table)
                            |> withStateNodeInstantiation(enclosing)
                            |> recordedConcreteCallType(2)(1)(1)([ExprNode(3)(ExprVar("x"))]))

let checkCopyRowAndScope unit =
    (let sourceRow = Some(SemVariable(8))
    in
        let actualRow = SemVariable(41)
        in
            let effect = SemRow([SemCapability("Inspect")([])])(None)
            in
                let enclosing = [(40, SemFloat)]
                in
                    match (recordedCopyInFrame(sourceRow)(Some(actualRow))([(8, actualRow)])(enclosing), recordedCopyInFrame(sourceRow)(Some(actualRow))([(8, actualRow)])([]), recordedCopyInFrame(Some(actualRow))(Some(actualRow))([])(enclosing), recordedCopyInFrame(sourceRow)(Some(effect))([(8, effect)])(enclosing)) with
                        | (Some(SemFunction(SemFloat, SemList(SemFloat), None)), None, None, None) -> Unit
                        | _ -> Ashes.IO.panic("concrete copy lost its enclosing instantiation, guessed an open type, or erased a capability"))

let runRecordedInferenceCoverageTests unit =
    unit
    |> checkHandlerBindings
    |> checkPrimitiveComplements
    |> checkArmBodyShadowing
    |> checkGuardedOrPattern
    |> checkRecordInstantiations
    |> checkConcreteCopyFacts
    |> checkRawNestedLets
    |> checkCopyRowAndScope
    |> checkTypeTranslation
    |> checkRigidScope
    |> checkDerivedTree
