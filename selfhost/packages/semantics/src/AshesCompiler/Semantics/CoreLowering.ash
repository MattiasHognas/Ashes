// Lowers the strict functional core into semantic IR before ownership placement.
//
// Invariants:
// - Callees are evaluated before arguments, and curried arguments are applied one at a time.
// - Conditions and match scrutinees are evaluated once; guarded arms retain source order.
// - Every function owns independent temp/local counters; closure env and argument locals are 0 and 1.
// - Captures follow first free-use order and occupy consecutive eight-byte environment words.
// - Recursive self/sibling references rebuild closures from the current shared environment.
// - Lifted functions retain generation order, with nested functions preceding their enclosing function.

import Ashes.Collection.List.map
import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.ProgramSyntax
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.NodeTypeTable.NodeTypeTable
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.SourceContext
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.StandardTraits.withIntrinsicBuiltinSignatures
import AshesCompiler.Semantics.ProgramInference.ProgramInferenceResult
import AshesCompiler.Semantics.ProgramInference.ProgramInferenceError
import AshesCompiler.Semantics.ProgramInference.inferProgramFromPackage
import AshesCompiler.Semantics.InferenceDiagnostics.inferenceErrorDiagnostic
import AshesCompiler.Semantics.MutualRecursionDispatch
import AshesCompiler.Semantics.ReuseFunctionSpecialization
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.RecursiveGroups
import AshesCompiler.Semantics.CoreLowering.Expressions
import AshesCompiler.Semantics.CoreLowering.Program
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.untranslatableRecordedTypes
// Raw executable callers have no identified inference result. Preserve their established
// lowering diagnostics except for an unresolved requirement needed during binding evaluation.
let validateRawBindingEvidence environment (program: ProgramSyntax) lower =
    (let builtins =
        match environment with
            | Some(provided) -> provided
            | None ->
                "standalone"
                |> emptyTypeEnvironmentForPackage
                |> withIntrinsicBuiltinSignatures
    in
        match inferProgramFromPackage("standalone")(builtins)(program) with
            | ProgramInferenceResult { error = Some(ProgramExpressionError(ImmediateTraitEvidenceRequired(_name, _args)) as error), errorLocation = location } ->
                match inferenceErrorDiagnostic([])(program.items)(builtins)(location)(error) with
                    | Some((_code, message)) -> failedCoreLowering(CoreImmediateEvidenceDiagnostic(message))
                    | None -> failedCoreLowering(CoreImmediateEvidenceDiagnostic("A trait requirement is needed while evaluating this binding. Add a type annotation to determine its instance."))
            | _ -> lower(program))

let lowerValidatedCoreProgram (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let trailingBody =
                match body with
                    | Some(expression) -> expression
                    | None -> ExprVar("Unit")
            in
                Unit
                |> initialState
                |> (given (state: CoreLoweringState) ->
                    state
                    |> withStateTopLevelNames(allTopLevelBindingNames(items)(Ashes.Collection.Map.empty))
                    |> withStateSpecializationCandidates(reuseSpecializationCandidates(items)))
                |> withProgramParameterOwnership(program)
                |> lowerProgramPasses(items)(trailingBody)(None)
                |> buildProgram

// As lowerCoreProgram, for a numbered program and the types inference recorded against it, as
// lowerCoreNumberedProgramWithSourceAndReuse takes them.
let lowerCoreProgram program = validateRawBindingEvidence(None)(program)(lowerValidatedCoreProgram)

let lowerCoreNumberedProgram (program: ProgramSyntax) (nodeTypes: NodeTypeTable) =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let trailingBody =
                match body with
                    | Some(expression) -> expression
                    | None -> ExprVar("Unit")
            in
                Unit
                |> initialState
                |> (given (state: CoreLoweringState) ->
                    state
                    |> withStateTopLevelNames(allTopLevelBindingNames(items)(Ashes.Collection.Map.empty))
                    |> withStateSpecializationCandidates(reuseSpecializationCandidates(items))
                    |> withNodeTypes(nodeTypes))
                |> withProgramParameterOwnership(program)
                |> lowerProgramPasses(items)(trailingBody)(None)
                |> buildProgram

// As lowerCoreProgramWithSource, but with caller-supplied `CoreConstructorLayout`/`CoreBuiltinLayout`
// lists — the same context `lowerCoreExpressionWithContext` already threads explicitly for a bare
// expression, parameterizing what `lowerCoreProgramWithSource` hardcodes to `[]`/`[]` via
// `initialState`. `ProgramSyntax` (this package's `parseProgram` output) carries no import
// information at all — imports are a project/multi-file-stitching concept handled entirely outside
// this single-file pipeline — so there is nothing here to derive a builtin's availability FROM; the
// caller (a project-mode driver, or a test exercising one specific builtin) must already know which
// `Ashes.*` builtins the program is entitled to call and construct their layouts directly.
// `constructorLayouts` needs a real entry for `"Unit"` too whenever any supplied builtin can
// return it (`finishBuiltinUnit` allocates a builtin's `Unit` result through an ordinary
// constructor layout, not a hardcoded shape — the same path a user's own zero-field ADT uses).
let lowerValidatedCoreProgramWithSourceAndContext (filePath: Str) (source: Str) (program: ProgramSyntax) (constructorLayouts: List(CoreConstructorLayout)) (builtinLayouts: List(CoreBuiltinLayout)) =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let trailingBody =
                match body with
                    | Some(expression) -> expression
                    | None -> ExprVar("Unit")
            in
                Unit
                |> initialStateWithContext(constructorLayouts)(builtinLayouts)
                |> (given (state: CoreLoweringState) ->
                    state
                    |> withStateSourceContext(source
                    |> createSourceContext(filePath)
                    |> Some)
                    |> withStateTopLevelNames(allTopLevelBindingNames(items)(Ashes.Collection.Map.empty))
                    |> withStateSpecializationCandidates(reuseSpecializationCandidates(items)))
                |> withProgramParameterOwnership(program)
                |> lowerProgramPasses(items)(trailingBody)(None)
                |> buildProgram

// The empty name stands for the trailing expression; spans start out in its region.
let lowerCoreProgramWithSourceAndContext path source program constructors builtins =
    validateRawBindingEvidence(None)(program)(given (validated) -> lowerValidatedCoreProgramWithSourceAndContext(path)(source)(validated)(constructors)(builtins))

let withSourceRegions (regions: SourceRegions) (state: CoreLoweringState) =
    state
    |> withStateSourceContext(Some(regions.context))
    |> withStateSourceRegions(regions.regionOfName)
    |> withStateSourceRegionOf("")

let lowerProgramWithRegionsAndReuse (reuseEnabled: Bool) (regions: SourceRegions) (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let trailingBody =
                match body with
                    | Some(expression) -> expression
                    | None -> ExprVar("Unit")
            in
                Unit
                |> initialStateWithContext(standardConstructorLayouts)(standardBuiltinLayouts)
                |> (given (state: CoreLoweringState) ->
                    state
                    |> withSourceRegions(regions)
                    |> withStateTopLevelNames(allTopLevelBindingNames(items)(Ashes.Collection.Map.empty))
                    |> withStateSpecializationCandidates(reuseSpecializationCandidates(items))
                    |> withStateReuseEnabled(reuseEnabled))
                |> withProgramParameterOwnership(program)
                |> lowerProgramPasses(items)(trailingBody)(None)
                |> buildProgram

// As lowerCoreProgramWithSource, with stage 0's `LoweringConfiguration.EnableReuse` switch: with
// `reuseEnabled` false (`--debug-disable-reuse`) no match arm publishes a reuse token and no
// binding is admitted to the reference-counted heap on the strength of a reuse-safe rebuild, so
// every cell is allocated fresh and released by its owner as before reuse.
let lowerProgramWithSourceAndReuse (reuseEnabled: Bool) (filePath: Str) (source: Str) (program: ProgramSyntax) =
    lowerProgramWithRegionsAndReuse(reuseEnabled)(singleSourceRegions(filePath)(source))(program)

// A program with a mutually recursive group that merges into one dispatch loop is lowered in its
// merged form. The merge does not know the members' types, so a merged program that fails to
// lower gives way to the program as written, whose own result, error included, stands.
// As lowerCoreProgramWithSourceAndReuse for a stitched program whose spans resolve in their own
// files' texts.
let lowerValidatedCoreProgramWithRegionsAndReuse (reuseEnabled: Bool) (regions: SourceRegions) (program: ProgramSyntax) =
    match mergeMutualRecursionGroups(program) with
        | None -> lowerProgramWithRegionsAndReuse(reuseEnabled)(regions)(program)
        | Some(merged) ->
            match lowerProgramWithRegionsAndReuse(reuseEnabled)(regions)(merged) with
                | CoreLoweringResult { error = None } as lowered -> lowered
                | _failed -> lowerProgramWithRegionsAndReuse(reuseEnabled)(regions)(program)

let lowerCoreProgramWithRegionsAndReuse reuseEnabled regions program =
    regions
    |> lowerValidatedCoreProgramWithRegionsAndReuse(reuseEnabled)
    |> validateRawBindingEvidence(None)(program)

let lowerCoreProgramWithSourceAndReuse reuseEnabled path source program =
    lowerCoreProgramWithRegionsAndReuse(reuseEnabled)(singleSourceRegions(path)(source))(program)

// As lowerProgramWithSourceAndReuse, for a program the standalone inference ran on first: its
// items are the numbered units with `deriving` already expanded, every expression node is wrapped
// in its identity, and `nodeTypes` holds what the inference recorded against those identities.
let lowerNumberedProgramWithRegionsAndReuse (reuseEnabled: Bool) (regions: SourceRegions) (nodeTypes: NodeTypeTable) (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let trailingBody =
                match body with
                    | Some(expression) -> expression
                    | None -> ExprVar("Unit")
            in
                Unit
                |> initialStateWithContext(standardConstructorLayouts)(standardBuiltinLayouts)
                |> (given (state: CoreLoweringState) ->
                    state
                    |> withSourceRegions(regions)
                    |> withStateTopLevelNames(allTopLevelBindingNames(items)(Ashes.Collection.Map.empty))
                    |> withStateSpecializationCandidates(reuseSpecializationCandidates(items))
                    |> withStateReuseEnabled(reuseEnabled)
                    |> withNodeTypes(nodeTypes))
                |> withProgramParameterOwnership(program)
                |> lowerProgramPasses(items)(trailingBody)(None)
                |> buildProgram

let lowerNumberedProgramWithSourceAndReuse (reuseEnabled: Bool) (filePath: Str) (source: Str) (nodeTypes: NodeTypeTable) (program: ProgramSyntax) =
    lowerNumberedProgramWithRegionsAndReuse(reuseEnabled)(singleSourceRegions(filePath)(source))(nodeTypes)(program)

// As lowerCoreNumberedProgramWithSourceAndReuse for a stitched program whose spans resolve in their
// own files' texts.
let lowerCoreNumberedProgramWithRegionsAndReuse (reuseEnabled: Bool) (regions: SourceRegions) (program: ProgramSyntax) (nodeTypes: NodeTypeTable) =
    match mergeMutualRecursionGroups(program) with
        | None -> lowerNumberedProgramWithRegionsAndReuse(reuseEnabled)(regions)(nodeTypes)(program)
        | Some(merged) ->
            match lowerNumberedProgramWithRegionsAndReuse(reuseEnabled)(regions)(nodeTypes)(merged) with
                | CoreLoweringResult { error = None } as lowered -> lowered
                | _failed -> lowerNumberedProgramWithRegionsAndReuse(reuseEnabled)(regions)(nodeTypes)(program)

// As lowerCoreProgramWithSourceAndReuse, merging mutually recursive groups the same way, for a
// numbered program and the types inference recorded against it.
let lowerCoreNumberedProgramWithSourceAndReuse (reuseEnabled: Bool) (filePath: Str) (source: Str) (program: ProgramSyntax) (nodeTypes: NodeTypeTable) =
    match mergeMutualRecursionGroups(program) with
        | None -> lowerNumberedProgramWithSourceAndReuse(reuseEnabled)(filePath)(source)(nodeTypes)(program)
        | Some(merged) ->
            match lowerNumberedProgramWithSourceAndReuse(reuseEnabled)(filePath)(source)(nodeTypes)(merged) with
                | CoreLoweringResult { error = None } as lowered -> lowered
                | _failed -> lowerNumberedProgramWithSourceAndReuse(reuseEnabled)(filePath)(source)(nodeTypes)(program)

// As lowerCoreProgram, but tags every emitted instruction with its source location — a plain,
// non-stitched single-file context, unlike lowerCoreExpressionLocated's stitched-project one.
let lowerCoreProgramWithSource (filePath: Str) (source: Str) (program: ProgramSyntax) = lowerCoreProgramWithSourceAndContext(filePath)(source)(program)(standardConstructorLayouts)(standardBuiltinLayouts)

// As lowerCoreProgram, but with a real inference TypeEnvironment supplied: a plain (non-recursive)
// top-level binding whose own generalized TypeScheme carries trait constraints has its value
// elaborated (rewriteTraitConstrainedValue) into hidden-dictionary-parameter form before lowering,
// rather than lowering the unrewritten constrained value as if it needed no evidence at all.
// Recursive top-level bindings and call-site trait-evidence forwarding
// (rewriteTraitConstrainedReference) are not yet wired — a deliberately narrower first slice of
// the trait-dictionary physical-lowering epic.
let lowerValidatedCoreProgramWithEnvironment (environment: TypeEnvironment) (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let trailingBody =
                match body with
                    | Some(expression) -> expression
                    | None -> ExprVar("Unit")
            in
                Unit
                |> initialState
                |> (given (state: CoreLoweringState) ->
                    state
                    |> withStateTopLevelNames(allTopLevelBindingNames(items)(Ashes.Collection.Map.empty))
                    |> withStateSpecializationCandidates(reuseSpecializationCandidates(items)))
                |> withProgramParameterOwnership(program)
                |> lowerProgramPasses(items)(trailingBody)(Some(environment))
                |> buildProgram

let lowerCoreProgramWithEnvironment environment program =
    environment
    |> lowerValidatedCoreProgramWithEnvironment
    |> validateRawBindingEvidence(Some(environment))(program)

// These entry points do not replay a discovery pass. Unresolved trait goals must fail
// here instead of allowing a provisional constant to escape into executable IR.
let lowerCoreExpression expression =
    Unit
    |> initialState
    |> withStateTraitGoalsPinned(true)
    |> withExpressionReachSummaries(expression)
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreExpressionWithLayouts layouts expression =
    Unit
    |> initialStateWithLayouts(layouts)
    |> withStateTraitGoalsPinned(true)
    |> withExpressionReachSummaries(expression)
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreExpressionWithContext constructorLayouts builtinLayouts expression =
    Unit
    |> initialStateWithContext(constructorLayouts)(builtinLayouts)
    |> withStateTraitGoalsPinned(true)
    |> withExpressionReachSummaries(expression)
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreExpressionWithFullContext constructorLayouts builtinLayouts externalLayouts externalFunctions externalOpaqueTypes expression =
    Unit
    |> initialStateWithFullContext(constructorLayouts)(builtinLayouts)(externalLayouts)(externalFunctions)(externalOpaqueTypes)
    |> withStateTraitGoalsPinned(true)
    |> withExpressionReachSummaries(expression)
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreExpressionWithCompleteContext constructorLayouts builtinLayouts externalLayouts externalFunctions externalOpaqueTypes capabilityLayouts staticProviders capabilityGlobalCount expression =
    Unit
    |> initialStateWithCompleteContext(constructorLayouts)(builtinLayouts)(externalLayouts)(externalFunctions)(externalOpaqueTypes)(capabilityLayouts)(staticProviders)(capabilityGlobalCount)
    |> withStateTraitGoalsPinned(true)
    |> withExpressionReachSummaries(expression)
    |> lowerCore(expression)
    |> buildProgram

// Lowers an expression that is (part of) the combined item at itemIndex of a stitched project,
// locating every emitted instruction through the context's item regions.
let lowerCoreExpressionLocated (context: SourceContext) (itemIndex: Int) expression =
    Unit
    |> initialState
    |> withStateTraitGoalsPinned(true)
    |> (given (state: CoreLoweringState) ->
        state
        |> withStateSourceContext(Some(context))
        |> withStateCurrentItem(itemIndex))
    |> withExpressionReachSummaries(expression)
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreRecursiveGroup bindings body =
    Unit
    |> initialState
    |> withStateTraitGoalsPinned(true)
    |> lowerRecursiveGroup(bindings)(body)(lowerCore)
    |> buildProgram

// What lowering concluded about a program's types, for a comparison with the standalone
// inference: each binding in scope at the trailing expression with its scheme, and each binary
// operator site lowered with its trait and operand type, both read through the final pass's
// substitution, with the error lowering stopped at, if any.
type CoreObservedTypes =
    | topLevel: List((Str, TypeScheme))
    | operators: List((Maybe(TextSpan), Str, SemanticType))
    | error: Maybe(CoreLoweringError)

let observedScheme (state: CoreLoweringState) (binding: CoreBinding) =
    (let scheme = binding.scheme
    in
        (binding.name, (scheme with body = resolveType(state)(scheme.body), constraints = map((given (constraint: TraitConstraint) ->
            constraint with typeArguments = map(resolveType(state))(constraint.typeArguments)))(scheme.constraints))))

let observedOperator (state: CoreLoweringState) (site: (Maybe(TextSpan), Str, SemanticType)) =
    match site with
        | (span, traitName, operandType) -> (span, traitName, resolveType(state)(operandType))

let observedTypesOf (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, error = error } ->
            match stateTypeObservation(state) with
                | None -> CoreObservedTypes(topLevel = [], operators = [], error = error)
                | Some(CoreTypeObservation { topLevelScope = scope, operators = operators }) ->
                    CoreObservedTypes(topLevel = map(observedScheme(state))(scope), operators = map(observedOperator(state))(operators), error = error)

// As lowerCoreProgramWithSourceAndReuse with reuse on, observing the types it settles; the
// lowered program itself is not built.
let lowerCoreProgramObservingTypes (filePath: Str) (source: Str) (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let trailingBody =
                match body with
                    | Some(expression) -> expression
                    | None -> ExprVar("Unit")
            in
                Unit
                |> initialStateWithContext(standardConstructorLayouts)(standardBuiltinLayouts)
                |> (given (state: CoreLoweringState) ->
                    state
                    |> withStateSourceContext(source
                    |> createSourceContext(filePath)
                    |> Some)
                    |> withStateTopLevelNames(allTopLevelBindingNames(items)(Ashes.Collection.Map.empty))
                    |> withStateSpecializationCandidates(reuseSpecializationCandidates(items))
                    |> withStateReuseEnabled(true)
                    |> withTypeObservation)
                |> withProgramParameterOwnership(program)
                |> lowerProgramPasses(items)(trailingBody)(None)
                |> observedTypesOf

// What one pass of a measured lowering counted: operator sites answered from the recorded node
// types, operator goals deferred or pended, function bodies lowered again, and calls whose expected
// result type came from the recorded node types and how many of those fell back, in the lowering
// that pass kept, with the error it stopped at, if any.
type CorePassCounts =
    | tableOperators: Int
    | deferredOperators: Int
    | relowerings: Int
    | tableCallResults: Int
    | tableFallbacks: Int
    | error: Maybe(CoreLoweringError)

type CoreLoweringMeasurement =
    | firstMillis: Int
    | replayMillis: Int
    | replayed: Bool
    | pendingGoals: Int
    | equalityGoals: Int
    | pendingGoalKeys: List(Str)
    | first: CorePassCounts
    | replay: CorePassCounts

let noPassCounts = CorePassCounts(tableOperators = 0, deferredOperators = 0, relowerings = 0, tableCallResults = 0, tableFallbacks = 0, error = None)

let passCountsOf (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, error = error } ->
            match stateTypeObservation(state) with
                | Some(CoreTypeObservation { tableOperators = tableOperators, deferredOperators = deferredOperators, relowerings = relowerings, tableCallResults = tableCallResults, tableFallbacks = tableFallbacks }) -> CorePassCounts(tableOperators = tableOperators, deferredOperators = deferredOperators, relowerings = relowerings, tableCallResults = tableCallResults, tableFallbacks = tableFallbacks, error = error)
                | None -> noPassCounts with error = error

// The seed every program-level lowering starts from with source locations and reuse on, given the
// recorded node types or none.
let measuredSeed (nodeTypes: Maybe(NodeTypeTable)) (filePath: Str) (source: Str) (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items } ->
            Unit
            |> initialStateWithContext(standardConstructorLayouts)(standardBuiltinLayouts)
            |> (given (state: CoreLoweringState) ->
                let configured =
                    state
                    |> withStateSourceContext(source
                    |> createSourceContext(filePath)
                    |> Some)
                    |> withStateTopLevelNames(allTopLevelBindingNames(items)(Ashes.Collection.Map.empty))
                    |> withStateSpecializationCandidates(reuseSpecializationCandidates(items))
                    |> withStateReuseEnabled(true)
                    |> withTypeObservation
                in
                    match nodeTypes with
                        | Some(table) -> withNodeTypes(table)(configured)
                        | None -> configured)
            |> withProgramParameterOwnership(program)

// As lowerCoreProgramWithSourceAndReuse with reuse on, or, given recorded node types, as
// lowerCoreNumberedProgramWithSourceAndReuse, timing the discovery pass and the replay pass with
// `clock` and counting what each kept; the lowered program itself is not built.
let measureCoreProgramLowering clock (nodeTypes: Maybe(NodeTypeTable)) (filePath: Str) (source: Str) (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let trailingBody =
                match body with
                    | Some(expression) -> expression
                    | None -> ExprVar("Unit")
            in
                match program
                |> measuredSeed(nodeTypes)(filePath)(source)
                |> lowerProgramPassesMeasured(clock)(items)(trailingBody)(None) with
                    | CoreProgramPassMeasurement { first = first, replay = Some(replayed), firstMillis = firstMillis, replayMillis = replayMillis, pendingGoals = pendingGoals, equalityGoals = equalityGoals, pendingGoalKeys = pendingGoalKeys } -> CoreLoweringMeasurement(firstMillis = firstMillis, replayMillis = replayMillis, replayed = true, pendingGoals = pendingGoals, equalityGoals = equalityGoals, pendingGoalKeys = pendingGoalKeys, first = passCountsOf(first), replay = passCountsOf(replayed))
                    | CoreProgramPassMeasurement { first = first, firstMillis = firstMillis } -> CoreLoweringMeasurement(firstMillis = firstMillis, replayMillis = 0, replayed = false, pendingGoals = 0, equalityGoals = 0, pendingGoalKeys = [], first = passCountsOf(first), replay = noPassCounts)

// Translates every type the standalone inference recorded into lowering's terms against the
// program's registered types: how many translated, and each node whose type did not, with the
// reason, or the error registration stopped at.
let checkRecordedTypes (nodeTypes: NodeTypeTable) (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items } ->
            match program
            |> measuredSeed(Some(nodeTypes))("")("")
            |> registeredProgramState(items) with
                | Error(error) -> Error(error)
                | Ok(registered) ->
                    registered
                    |> untranslatableRecordedTypes(nodeTypes)
                    |> Ok
