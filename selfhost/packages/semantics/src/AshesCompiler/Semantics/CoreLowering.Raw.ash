// The lowering's raw entry points: a program or an expression lowered without the standalone
// inference's node types, by lowering's own discovery pass, replay, pending goals and deferred
// operators. Test-only: the compiler lowers through `CoreLowering`'s numbered entry points, and no
// module the command line imports imports this one. It stays for the lowered-IR parity fixtures
// against stage 0 and the expression-level tests until they move to the typed path.

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
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.rawOperatorDeclarationSchemes
import AshesCompiler.Semantics.CoreLowering.RecordedTypes.withRawOperatorDeclarationSchemes
import AshesCompiler.Semantics.CoreLowering.withSourceRegions
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
            | ProgramInferenceResult { nodeTypes = table, error = None } ->
                lower(rawOperatorDeclarationSchemes(table))(program)
            | _ -> lower(Ashes.Collection.Map.empty)(program))

let lowerValidatedCoreProgram schemes (program: ProgramSyntax) =
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
                    |> withRawOperatorDeclarationSchemes(schemes)
                    |> withStateTopLevelNames(allTopLevelBindingNames(items)(Ashes.Collection.Map.empty))
                    |> withStateSpecializationCandidates(reuseSpecializationCandidates(items)))
                |> withProgramParameterOwnership(program)
                |> lowerProgramPasses(items)(trailingBody)(None)
                |> buildProgram

let lowerCoreProgram program = validateRawBindingEvidence(None)(program)(lowerValidatedCoreProgram)

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
let lowerValidatedCoreProgramWithSourceAndContext schemes (filePath: Str) (source: Str) (program: ProgramSyntax) (constructorLayouts: List(CoreConstructorLayout)) (builtinLayouts: List(CoreBuiltinLayout)) =
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
                    |> withRawOperatorDeclarationSchemes(schemes)
                    |> withStateTopLevelNames(allTopLevelBindingNames(items)(Ashes.Collection.Map.empty))
                    |> withStateSpecializationCandidates(reuseSpecializationCandidates(items)))
                |> withProgramParameterOwnership(program)
                |> lowerProgramPasses(items)(trailingBody)(None)
                |> buildProgram

// The empty name stands for the trailing expression; spans start out in its region.
let lowerCoreProgramWithSourceAndContext path source program constructors builtins =
    validateRawBindingEvidence(None)(program)(given (schemes) ->
        given (validated) -> lowerValidatedCoreProgramWithSourceAndContext(schemes)(path)(source)(validated)(constructors)(builtins))

let lowerProgramWithRegionsAndSchemes schemes (reuseEnabled: Bool) (regions: SourceRegions) (program: ProgramSyntax) =
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
                    |> withRawOperatorDeclarationSchemes(schemes)
                    |> withStateTopLevelNames(allTopLevelBindingNames(items)(Ashes.Collection.Map.empty))
                    |> withStateSpecializationCandidates(reuseSpecializationCandidates(items))
                    |> withStateReuseEnabled(reuseEnabled))
                |> withProgramParameterOwnership(program)
                |> lowerProgramPasses(items)(trailingBody)(None)
                |> buildProgram

let lowerProgramWithRegionsAndReuse reuseEnabled regions program = lowerProgramWithRegionsAndSchemes(Ashes.Collection.Map.empty)(reuseEnabled)(regions)(program)

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
let lowerValidatedCoreProgramWithSchemesAndReuse (reuseEnabled: Bool) (regions: SourceRegions) schemes (program: ProgramSyntax) =
    match mergeMutualRecursionGroups(program) with
        | None -> lowerProgramWithRegionsAndSchemes(schemes)(reuseEnabled)(regions)(program)
        | Some(merged) ->
            match lowerProgramWithRegionsAndSchemes(schemes)(reuseEnabled)(regions)(merged) with
                | CoreLoweringResult { error = None } as lowered -> lowered
                | _failed -> lowerProgramWithRegionsAndSchemes(schemes)(reuseEnabled)(regions)(program)

let lowerValidatedCoreProgramWithRegionsAndReuse reuseEnabled regions program = lowerValidatedCoreProgramWithSchemesAndReuse(reuseEnabled)(regions)(Ashes.Collection.Map.empty)(program)

let lowerCoreProgramWithRegionsAndReuse reuseEnabled regions program =
    regions
    |> lowerValidatedCoreProgramWithSchemesAndReuse(reuseEnabled)
    |> validateRawBindingEvidence(None)(program)

let lowerCoreProgramWithSourceAndReuse reuseEnabled path source program =
    lowerCoreProgramWithRegionsAndReuse(reuseEnabled)(singleSourceRegions(path)(source))(program)

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
let lowerValidatedCoreProgramWithEnvironment (environment: TypeEnvironment) schemes (program: ProgramSyntax) =
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
                    |> withRawOperatorDeclarationSchemes(schemes)
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
