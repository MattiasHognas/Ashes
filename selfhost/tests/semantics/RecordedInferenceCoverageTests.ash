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
import AshesCompiler.Semantics.CoreLowering.measureCoreProgramLoweringWithGoalDetails
import AshesCompiler.Semantics.CoreLowering.measureStitchedLowering
import AshesCompiler.Semantics.CoreLowering.Emit.CoreBinaryOperator
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.rawOperatorDeclarationSchemes
import AshesCompiler.Semantics.CoreLowering.Matches.resultMayViewCallArgument
import AshesCompiler.Semantics.CoreLowering.CallArguments.applyRecordedLambdaType
import AshesCompiler.Semantics.CoreLowering.Emit.resolveType
import AshesCompiler.Semantics.CoreLowering.LoweringState.CoreLoweringState
import AshesCompiler.Semantics.CoreLowering.Operators.observeDeferredOperator
import AshesCompiler.Semantics.CoreLowering.LoweringState.CoreTypeObservation
import AshesCompiler.Semantics.CoreLowering.LoweringState.ObservedPendingGoal
import AshesCompiler.Semantics.CoreLowering.LoweringState.withTypeObservation
import AshesCompiler.Semantics.CoreLowering.LoweringState.withPendingGoalObservation
import AshesCompiler.Semantics.CoreLowering.LoweringState.stateTypeObservation
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
        in checkRawNestedLetCopies("captured by an inner lambda")("let describe n =\n    (let double x = x + x\n    in\n        let both = given (k) -> (double(n), double(1.75), k)\n        in both(0))\ndescribe(6)"))

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

let observedGoalSites state =
    match stateTypeObservation(state) with
        | Some(CoreTypeObservation { goalSites = Some(sites) }) -> sites
        | _ -> []

// A deferred `+` is named under its trait and method when its site has no span, an operator
// lowering never defers names nothing, and a lowering that observes nothing is left without an
// observation.
let checkDeferredOperatorObservation unit =
    (let observed =
        Unit
        |> initialState
        |> withTypeObservation
        |> withPendingGoalObservation(Ashes.Collection.Map.empty)
    in
        let added = observeDeferredOperator(CoreAddOperator)(SemVariable(5))(observed)
        in
            let divided = observeDeferredOperator(CoreDivideOperator)(SemVariable(6))(observed)
            in
                let unobserved =
                    Unit
                    |> initialState
                    |> observeDeferredOperator(CoreAddOperator)(SemVariable(5))
                in
                    match (observedGoalSites(added), observedGoalSites(divided), stateTypeObservation(unobserved)) with
                        | (ObservedPendingGoal { key = key, structural = structural, deferredOperator = deferredOperator, operand = operand } :: [], [], None) ->
                            if key == "unspanned:Add:add" && structural == false && deferredOperator && operand == SemVariable(5)
                            then Unit
                            else Ashes.IO.panic("deferred operator site recorded the wrong key, kind or operand")
                        | _ -> Ashes.IO.panic("deferred operator observation recorded the wrong number of sites or created an observation"))

let recursive countDeferredOperatorSites (details: List((ObservedPendingGoal, SemanticType))) count =
    match details with
        | [] -> count
        | (ObservedPendingGoal { deferredOperator = deferredOperator }, _resolved) :: rest ->
            if deferredOperator
            then countDeferredOperatorSites(rest)(count + 1)
            else countDeferredOperatorSites(rest)(count)

// Every operator the first pass deferred over two open operands is named in the goal details, also
// when no replay runs: the program has no ordered comparison, so the pass's deferred operator count
// is exactly its speculative arithmetic sites.
let checkDeferredOperatorDetails unit =
    match parseProgram("let twice x = [x + x, x]\n(twice(1), twice(1.5))") with
        | ProgramParseResult { program = parsed, diagnostics = [] } ->
            match identifyProgram(0)(parsed) with
                | (program, _count) ->
                    match inferProgramFromPackage("standalone")(standardTraitEnvironment(Unit))(program) with
                        | ProgramInferenceResult { nodeTypes = table, error = None } ->
                            let clock =
                                given (_unit) -> 0
                            in
                                let typed = measureCoreProgramLoweringWithGoalDetails(Ashes.Collection.Map.empty)(clock)(Some(table))("details.ash")("")(program)
                                in
                                    let legacy =
                                        measureCoreProgramLoweringWithGoalDetails(rawOperatorDeclarationSchemes(table))(clock)(None)("details.ash")("")(program)
                                    in
                                        let firstTyped = typed.first
                                        in
                                            let firstLegacy = legacy.first
                                            in
                                                let typedSites = countDeferredOperatorSites(typed.goalDetails)(0)
                                                in
                                                    let legacySites = countDeferredOperatorSites(legacy.goalDetails)(0)
                                                    in
                                                        if firstTyped.error == None && firstLegacy.error == None && typedSites == firstTyped.deferredOperators && legacySites == firstLegacy.deferredOperators
                                                        then Unit
                                                        else Ashes.IO.panic("deferred operator details: typed sites=" + Ashes.Text.fromInt(typedSites) + " of " + Ashes.Text.fromInt(firstTyped.deferredOperators) + ", legacy sites=" + Ashes.Text.fromInt(legacySites) + " of " + Ashes.Text.fromInt(firstLegacy.deferredOperators))
                        | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("deferred operator inference failed: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic("deferred operator fixture did not parse")

// A partly open equality lowering meets before the element type is known, in a program that uses
// the binding at one element type: the typed first pass answers it from the table, leaves no trait
// goal and runs no replay, while the legacy lowering of the same program still leaves a goal for its
// replay, whichever counter carries it (`noReplay`); where another goal of the program still replays,
// the typed replay must lower the site too. The program is stitched with the shipped modules, as
// `compile` does, so the list and string instances of `Ashes.Trait` it compares through exist.
// Both modes must lower without an error.
let checkTypedEqualityLeavesNoGoal (label: Str) (noReplay: Bool) (source: Str) =
    match Ashes.IO.Directory.entries("lib/Ashes") with
        | Error(message) -> Ashes.IO.panic(message)
        | Ok(names) ->
            match names
            |> readShippedSources
            |> stitchWithShippedModules("equality")("equality.ash")(source) with
                | Error(error) -> Ashes.IO.panic(label + " fixture did not stitch: " + Ashes.Trait.Show.show(error))
                | Ok(stitched) ->
                    let identified = identifyStitchedProject(stitched)
                    in
                        match inferIdentifiedStitchedProjectFrom("standalone"
                        |> emptyTypeEnvironmentForPackage
                        |> withIntrinsicBuiltinSignatures)(stitched)(identified) with
                            | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic(label + " inference failed: " + Ashes.Trait.Show.show(error))
                            | ProgramInferenceResult { nodeTypes = table, error = None } ->
                                let clock =
                                    given (_unit) -> 0
                                in
                                    let typed =
                                        identified.program
                                        |> identifiedProgramSyntax
                                        |> measureStitchedLowering(true)(None)(given (_) -> Unit)(clock)(table)("equality.ash")
                                    in
                                        let legacy =
                                            measureStitchedLowering(false)(None)(given (_) -> Unit)(clock)(table)("equality.ash")(stitched.program)
                                        in
                                            let firstTyped = typed.first
                                            in
                                                let firstLegacy = legacy.first
                                                in
                                                    let replayLegacy = legacy.replay
                                                    in
                                                        let replayTyped = typed.replay
                                                        in
                                                            if firstTyped.error == None && replayTyped.error == None && firstLegacy.error == None && replayLegacy.error == None && typed.equalityGoals == 0 && (if noReplay
                                                            then typed.pendingGoals == 0 && typed.replayed == false && legacy.pendingGoals + legacy.equalityGoals > 0
                                                            else true)
                                                            then Unit
                                                            else
                                                                Ashes.IO.panic(label + ": typed pending=" + Ashes.Text.fromInt(typed.pendingGoals) + " equality=" + Ashes.Text.fromInt(typed.equalityGoals) + " replayed=" + (if typed.replayed
                                                                then "yes"
                                                                else "no") + " first error=" + Ashes.Trait.Show.show(firstTyped.error) + " replay error=" + Ashes.Trait.Show.show(replayTyped.error) + ", legacy pending=" + Ashes.Text.fromInt(legacy.pendingGoals) + " equality=" + Ashes.Text.fromInt(legacy.equalityGoals) + " first error=" + Ashes.Trait.Show.show(firstLegacy.error) + " replay error=" + Ashes.Trait.Show.show(replayLegacy.error))

// `xs == []` in a recursive binding used at `List(Str)`.
let checkPartlyOpenEqualitySettled unit = checkTypedEqualityLeavesNoGoal("partly open equality")(true)("let recursive countUntilEmpty xs n =\n    if xs == []\n    then n\n    else\n        match xs with\n            | _ :: rest -> countUntilEmpty(rest)(n + 1)\n            | [] -> n\ncountUntilEmpty([\"a\", \"b\", \"c\"])(0) + countUntilEmpty([\"d\"])(0)")

// `selected != []` in a constrained helper used only from a generic wrapper: the first pass compares
// through the helper's own Eq dictionary, and the wrapper's dictionary for the helper, which lowering
// asks as `Eq(List(a))` where the table records the reduced `Eq(a)`, takes the settled element
// type, so no goal is left and no replay runs.
let checkWrappedInequalitySettled unit = checkTypedEqualityLeavesNoGoal("wrapped partly open inequality")(true)("let hasAny selected =\n    if selected != []\n    then \"some\"\n    else \"none\"\nlet describe name selected = name + \":\" + hasAny(selected)\ndescribe(\"a\")([\"x\"]) + \" \" + describe(\"b\")([\"y\", \"z\"])")

let trailingBody (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { body = Some(body) } -> body
        | _ -> Ashes.IO.panic("fixture has no trailing expression")

// The view test of a `let` result sees through the identity wrappers of a numbered body: a match on
// a call reading `text` can hand back a view of it, numbered or not.
let checkNumberedResultView unit =
    match parseProgram("let text = \"ab\"\nmatch Ashes.Text.unconsText(text) with\n    | Some((_head, tail)) -> tail\n    | None -> \"\"") with
        | ProgramParseResult { program = parsed, diagnostics = [] } ->
            match identifyProgram(0)(parsed) with
                | (program, _count) ->
                    if resultMayViewCallArgument("text")(0)(trailingBody(parsed)) && resultMayViewCallArgument("text")(0)(trailingBody(program))
                    then Unit
                    else Ashes.IO.panic("a numbered let body lost the view of its scrutinized call argument")
        | _ -> Ashes.IO.panic("result view fixture did not parse")

// The same wrapper shape with `==`, the shape that used to reach the replay without a proof.
let checkWrappedEqualitySettled unit = checkTypedEqualityLeavesNoGoal("wrapped partly open equality")(true)("let isEmpty selected = selected == []\nlet describe name selected =\n    if isEmpty(selected)\n    then name\n    else \"x\"\ndescribe(\"a\")([\"x\"])")

// A lambda's parameter takes the closed type recorded for it at its own node before its body is
// lowered, under typed lowering only, and only when the node binds a parameter of that name.
let checkRecordedLambdaParameter unit =
    (let table = emptyNodeTypeTable with bindings = Ashes.Collection.Map.setInt(5)([("x", TypeScheme(quantified = [], body = SemString, constraints = []))])(Ashes.Collection.Map.empty)
    in
        let typed =
            Unit
            |> initialState
            |> withNodeTypes(table)
            |> (given (state: CoreLoweringState) -> state with currentNode = Some(5))
        in
            let bound = applyRecordedLambdaType("x")(SemVariable(90001))(typed)
            in
                let otherName = applyRecordedLambdaType("y")(SemVariable(90001))(typed)
                in
                    let untyped = applyRecordedLambdaType("x")(SemVariable(90001))((initialState(Unit) with currentNode = Some(5)))
                    in
                        if resolveType(bound)(SemVariable(90001)) == SemString && resolveType(otherName)(SemVariable(90001)) == SemVariable(90001) && resolveType(untyped)(SemVariable(90001)) == SemVariable(90001)
                        then Unit
                        else Ashes.IO.panic("a lambda parameter did not take its recorded type, or took one from another binding or without a table"))

// A test program whose requirements the enclosing function supplies (a local `Seed.first(Unit)` the
// body later compares with its parameter, a callback returned by a constrained helper) or whose
// escaping constrained reference the program uses at one type: the typed first pass leaves no goal.
let checkTestProgramLeavesNoGoal (name: Str) =
    match Ashes.IO.File.readText("tests/" + name + ".ash") with
        | Ok(source) -> checkTypedEqualityLeavesNoGoal(name)(true)(source)
        | Error(message) -> Ashes.IO.panic(message)

let checkEnclosingEvidence unit =
    (let _ = checkTestProgramLeavesNoGoal("local_eager_seed_uses_evidence_of_its_enclosing_function")
    in
        let _ = checkTestProgramLeavesNoGoal("returned_callback_keeps_evidence_until_invoked")
        in checkTestProgramLeavesNoGoal("escaping_recursive_function_applied_with_evidence"))

// A synthesized identity is derived from its source node and role alone and maps back to both; it
// never collides with a numbered node or the no-identity marker.
let checkSynthesizedIdentity unit =
    (let prefix = synthesizedNodeIdentity(1037)(partialPrefixRole)
    in
        let resume = synthesizedNodeIdentity(1037)(partialResumeRole)
        in
            if prefix < -1 && resume < -1 && prefix != resume && synthesizedNodeIdentity(1037)(partialPrefixRole) == prefix && synthesizedNodeSource(prefix) == Some((1037, partialPrefixRole)) && synthesizedNodeSource(resume) == Some((1037, partialResumeRole)) && synthesizedNodeSource(0) == None && synthesizedNodeSource(-1) == None && synthesizedNodeIdentity(-1)(partialPrefixRole) == -1 && synthesizedNodeSource(synthesizedNodeIdentity(0)(partialResumeRole)) == Some((0, partialResumeRole))
            then Unit
            else Ashes.IO.panic("synthesized node identities do not round-trip to their source and role"))

// A staged binding's call split into its prefix and resume functions reads the dictionaries recorded
// at the source call's reference: an annotated partial application of a constrained staged binding.
let checkPartialStageEvidence unit = checkTestProgramLeavesNoGoal("annotated_partial_resolves_evidence_before_returning_function")

// A top-level binding the inference generalized over an arithmetic trait and the program uses at two
// types (`square` at Int and Float, the recursive `sumWith` and `addTimes`): under typed lowering it is
// lowered once taking the operator's dictionary, so the typed first pass defers no operator, where the
// legacy lowering emits its operator in the speculative integer form. Both modes lower without error.
let checkArithmeticDictionaryProgram (name: Str) =
    (let sourcePath = "tests/" + name + ".ash"
    in
        match (Ashes.IO.Directory.entries("lib/Ashes"), Ashes.IO.File.readText(sourcePath)) with
            | (Ok(names), Ok(source)) ->
                match names
                |> readShippedSources
                |> stitchWithShippedModules(name)(name + ".ash")(source) with
                    | Error(error) -> Ashes.IO.panic(name + " did not stitch: " + Ashes.Trait.Show.show(error))
                    | Ok(stitched) ->
                        let identified = identifyStitchedProject(stitched)
                        in
                            match inferIdentifiedStitchedProjectFrom("standalone"
                            |> emptyTypeEnvironmentForPackage
                            |> withIntrinsicBuiltinSignatures)(stitched)(identified) with
                                | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic(name + " inference failed: " + Ashes.Trait.Show.show(error))
                                | ProgramInferenceResult { nodeTypes = table, error = None } ->
                                    let clock =
                                        given (_unit) -> 0
                                    in
                                        let typed =
                                            identified.program
                                            |> identifiedProgramSyntax
                                            |> measureStitchedLowering(true)(None)(given (_) -> Unit)(clock)(table)(name + ".ash")
                                        in
                                            let legacy =
                                                measureStitchedLowering(false)(None)(given (_) -> Unit)(clock)(table)(name + ".ash")(stitched.program)
                                            in
                                                let firstTyped = typed.first
                                                in
                                                    let firstLegacy = legacy.first
                                                    in
                                                        if firstTyped.error == None && firstLegacy.error == None && firstTyped.deferredOperators == 0 && typed.replayed == false && firstLegacy.deferredOperators > 0
                                                        then Unit
                                                        else Ashes.IO.panic(name + ": typed deferred=" + Ashes.Text.fromInt(firstTyped.deferredOperators) + " first error=" + Ashes.Trait.Show.show(firstTyped.error) + ", legacy deferred=" + Ashes.Text.fromInt(firstLegacy.deferredOperators) + " first error=" + Ashes.Trait.Show.show(firstLegacy.error))
            | (Error(message), _) -> Ashes.IO.panic(message)
            | (_, Error(message)) -> Ashes.IO.panic(message))

let checkArithmeticDictionaryBinding unit =
    (let _ = checkArithmeticDictionaryProgram("generic_multiply_binding_used_at_int_and_float")
    in checkArithmeticDictionaryProgram("generic_add_non_tail_recursive_binding_used_at_int_and_float"))

let instantiationAt (arguments: List((Int, SemanticType))) = NodeInstantiation(arguments = arguments, requirements = [], dictionaries = [], evidenceDemands = [])

// A binding referenced only at another binding's quantified variable is settled through that
// binding's own uses: 10 through 20 at Str; 30 is used at Float directly and at 40 (Int), so it stays
// open; 50 waits on a variable no reference settles; 70 is used at two types.
let checkTransitiveSettlement unit =
    (let instantiations =
        Ashes.Collection.Map.empty
        |> Ashes.Collection.Map.setInt(1)(instantiationAt([(10, SemList(SemVariable(20)))]))
        |> Ashes.Collection.Map.setInt(2)(instantiationAt([(20, SemString)]))
        |> Ashes.Collection.Map.setInt(3)(instantiationAt([(30, SemVariable(40))]))
        |> Ashes.Collection.Map.setInt(4)(instantiationAt([(40, SemInt)]))
        |> Ashes.Collection.Map.setInt(5)(instantiationAt([(30, SemFloat)]))
        |> Ashes.Collection.Map.setInt(6)(instantiationAt([(50, SemVariable(60))]))
        |> Ashes.Collection.Map.setInt(7)(instantiationAt([(70, SemInt), (80, SemVariable(20))]))
        |> Ashes.Collection.Map.setInt(8)(instantiationAt([(70, SemBool)]))
    in
        let settled = settledInstantiationArguments((emptyNodeTypeTable with instantiations = instantiations))
        in
            match (Ashes.Collection.Map.getInt(10)(settled), Ashes.Collection.Map.getInt(20)(settled), Ashes.Collection.Map.getInt(30)(settled), Ashes.Collection.Map.getInt(40)(settled), Ashes.Collection.Map.getInt(50)(settled), Ashes.Collection.Map.getInt(70)(settled), Ashes.Collection.Map.getInt(80)(settled)) with
                | (Some(SemList(SemString)), Some(SemString), None, Some(SemInt), None, None, Some(SemString)) -> Unit
                | _ -> Ashes.IO.panic("settled instantiation arguments did not follow settled enclosing variables, or settled a contradicted or open one"))

let runRecordedInferenceCoverageTests unit =
    unit
    |> checkTransitiveSettlement
    |> checkNumberedResultView
    |> checkPartlyOpenEqualitySettled
    |> checkWrappedInequalitySettled
    |> checkWrappedEqualitySettled
    |> checkRecordedLambdaParameter
    |> checkEnclosingEvidence
    |> checkSynthesizedIdentity
    |> checkPartialStageEvidence
    |> checkArithmeticDictionaryBinding
    |> checkDeferredOperatorObservation
    |> checkDeferredOperatorDetails
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
