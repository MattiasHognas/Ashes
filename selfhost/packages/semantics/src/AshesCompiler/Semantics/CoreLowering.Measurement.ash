// The measured lowering the typed-lowering measurement driver and the recorded-inference coverage
// tests use: a program lowered with type observation on, timing and counting its passes, given the
// recorded node types or (for a raw comparison) none. Not imported by the command line.

import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.ProgramSyntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.NodeTypeTable.NodeTypeTable
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.SourceContext
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.MutualRecursionDispatch
import AshesCompiler.Semantics.ReuseFunctionSpecialization
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.RecursiveGroups
import AshesCompiler.Semantics.CoreLowering.Expressions
import AshesCompiler.Semantics.CoreLowering.Program
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.untranslatableRecordedTypes
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.withRawOperatorDeclarationSchemes
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.rawOperatorSchemesWithNestedLets
import AshesCompiler.Semantics.CallOwnership.compiledResultRuntimeManaged
import Ashes.Collection.List.reverse
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
    | recordedCopyCalls: Int
    | inferredCopyCalls: Int
    // The step-8 shadow comparison of the pass: agreeing sites per rule (1 to 12, 12 being entry 8c) and every other site.
    | shadowAgree: List(Int)
    | shadowSites: List(ShadowSite)
    | error: Maybe(CoreLoweringError)

type CoreLoweringMeasurement =
    | firstMillis: Int
    | replayMillis: Int
    | replayed: Bool
    | pendingGoals: Int
    | equalityGoals: Int
    | pendingGoalKeys: List(Str)
    | equalityGoalKeys: List(Str)
    | retainedRelowerings: Maybe(Int)
    | goalDetails: List((ObservedPendingGoal, SemanticType))
    | first: CorePassCounts
    | replay: CorePassCounts

let noPassCounts = CorePassCounts(tableOperators = 0, deferredOperators = 0, relowerings = 0, tableCallResults = 0, tableFallbacks = 0, recordedCopyCalls = 0, inferredCopyCalls = 0, shadowAgree = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], shadowSites = [], error = None)

let recursive incrementShadowAgree (index: Int) (counts: List(Int)) =
    match counts with
        | [] -> []
        | head :: rest ->
            if index == 0
            then head + 1 :: rest
            else head :: incrementShadowAgree(index - 1)(rest)

// The shadow sites of a pass with rule 9's settled once every body is lowered: a callee asked for
// an arena result because its body was not lowered yet, whose result fact the finished lowering
// knows, is a call the rule over the final facts would not have asked; one whose fact is still
// unknown agrees.
let recursive settledShadowSites (state: CoreLoweringState) (sites: List(ShadowSite)) (agree: List(Int)) (kept: List(ShadowSite)) =
    match sites with
        | [] -> (agree, reverse(kept))
        | (ShadowSite { calleeFacts = Some(_facts) } as site) :: rest ->
            match state
            |> stateBodyRuntimeManagedByLabel
            |> compiledResultRuntimeManaged(site.calleeFacts)(stateFunctionReturnedClosureLabels(state)) with
                | Some(_known) -> settledShadowSites(state)(rest)(agree)((site with final = Some("no-arena")) :: kept)
                | None ->
                    settledShadowSites(state)(rest)(incrementShadowAgree(site.entry - 1)(agree))(kept)
        | site :: rest -> settledShadowSites(state)(rest)(agree)(site :: kept)

let passCountsOf (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, error = error } ->
            match stateTypeObservation(state) with
                | Some(CoreTypeObservation { tableOperators = tableOperators, deferredOperators = deferredOperators, relowerings = relowerings, tableCallResults = tableCallResults, tableFallbacks = tableFallbacks, recordedCopyCalls = recordedCopies, inferredCopyCalls = inferredCopies, shadowAgree = agree, shadowSites = sites }) ->
                    match settledShadowSites(state)(sites)(agree)([]) with
                        | (settledAgree, settledSites) -> CorePassCounts(tableOperators = tableOperators, deferredOperators = deferredOperators, relowerings = relowerings, tableCallResults = tableCallResults, tableFallbacks = tableFallbacks, recordedCopyCalls = recordedCopies, inferredCopyCalls = inferredCopies, shadowAgree = settledAgree, shadowSites = settledSites, error = error)
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
// `clock` and counting what each kept; the lowered program itself is not built. `single` keeps the
// first lowering of every body (measureStitchedLoweringOnce).
let measureCoreProgramLoweringObservedOnce (single: Bool) regionOfName replayStarting schemes clock (nodeTypes: Maybe(NodeTypeTable)) (filePath: Str) (source: Str) (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let trailingBody =
                match body with
                    | Some(expression) -> expression
                    | None -> ExprVar("Unit")
            in
                match program
                |> measuredSeed(nodeTypes)(filePath)(source)
                |> withRawOperatorDeclarationSchemes(schemes)
                |> withSingleLoweringMeasurement(single)
                |> (given (seed) ->
                    match regionOfName with
                        | None -> seed
                        | Some(regions) -> withPendingGoalObservation(regions)(seed))
                |> lowerProgramPassesMeasuredWithProgress(replayStarting)(clock)(items)(trailingBody)(None) with
                    | CoreProgramPassMeasurement { first = first, replay = Some(replayed), firstMillis = firstMillis, replayMillis = replayMillis, pendingGoals = pendingGoals, equalityGoals = equalityGoals, pendingGoalKeys = pendingGoalKeys, equalityGoalKeys = equalityGoalKeys, retainedRelowerings = retained, goalDetails = details } -> CoreLoweringMeasurement(firstMillis = firstMillis, replayMillis = replayMillis, replayed = true, pendingGoals = pendingGoals, equalityGoals = equalityGoals, pendingGoalKeys = pendingGoalKeys, equalityGoalKeys = equalityGoalKeys, retainedRelowerings = retained, goalDetails = details, first = passCountsOf(first), replay = passCountsOf(replayed))
                    | CoreProgramPassMeasurement { first = first, firstMillis = firstMillis, goalDetails = details } -> CoreLoweringMeasurement(firstMillis = firstMillis, replayMillis = 0, replayed = false, pendingGoals = 0, equalityGoals = 0, pendingGoalKeys = [], equalityGoalKeys = [], retainedRelowerings = None, goalDetails = details, first = passCountsOf(first), replay = noPassCounts)

let measureCoreProgramLoweringObserved regionOfName replayStarting schemes clock (nodeTypes: Maybe(NodeTypeTable)) (filePath: Str) (source: Str) (program: ProgramSyntax) = measureCoreProgramLoweringObservedOnce(false)(regionOfName)(replayStarting)(schemes)(clock)(nodeTypes)(filePath)(source)(program)

let measureCoreProgramLoweringWithSchemes schemes clock nodeTypes path source program =
    measureCoreProgramLoweringObserved(None)(given (_) -> Unit)(schemes)(clock)(nodeTypes)(path)(source)(program)

// As measureStitchedLowering under the type table with every body lowered once: the first
// lowering of a body that asks to be lowered again is kept. Its time against the ordinary lowering's
// is what re-lowering (step-8 rules 4 and 10) costs; the lowered program is not meant to be used.
let measureStitchedLoweringOnce regionOfName replayStarting clock table path program = measureCoreProgramLoweringObservedOnce(true)(regionOfName)(replayStarting)(Ashes.Collection.Map.empty)(clock)(Some(table))(path)("")(program)

let measureStitchedLowering useNodeTypes regionOfName replayStarting clock table path program =
    if useNodeTypes
    then measureCoreProgramLoweringObserved(regionOfName)(replayStarting)(Ashes.Collection.Map.empty)(clock)(Some(table))(path)("")(program)
    else
        measureCoreProgramLoweringObserved(regionOfName)(replayStarting)(rawOperatorSchemesWithNestedLets(Ashes.Collection.Map.empty)(program)(table))(clock)(None)(path)("")(program)

let measureCoreProgramLowering clock nodeTypes path source program = measureCoreProgramLoweringWithSchemes(Ashes.Collection.Map.empty)(clock)(nodeTypes)(path)(source)(program)

// As measureCoreProgramLoweringWithSchemes, also collecting the goal sites the first pass left open,
// for a single-file program whose spans all resolve in the entry region.
let measureCoreProgramLoweringWithGoalDetails schemes clock nodeTypes path source program =
    measureCoreProgramLoweringObserved(Some(Ashes.Collection.Map.empty))(given (_) -> Unit)(schemes)(clock)(nodeTypes)(path)(source)(program)

let measureRawCoreProgramLowering clock table path source program =
    measureCoreProgramLoweringWithSchemes(rawOperatorSchemesWithNestedLets(Ashes.Collection.Map.empty)(program)(table))(clock)(None)(path)(source)(program)

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
