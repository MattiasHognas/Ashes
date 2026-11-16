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
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.ProgramSyntax
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.SourceContext
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.ReuseFunctionSpecialization
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.RecursiveGroups
import AshesCompiler.Semantics.CoreLowering.Expressions
import AshesCompiler.Semantics.CoreLowering.Program
let lowerCoreProgram (program: ProgramSyntax) =
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
let lowerCoreProgramWithSourceAndContext (filePath: Str) (source: Str) (program: ProgramSyntax) (constructorLayouts: List(CoreConstructorLayout)) (builtinLayouts: List(CoreBuiltinLayout)) =
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

// As lowerCoreProgramWithSource, with stage 0's `LoweringConfiguration.EnableReuse` switch: with
// `reuseEnabled` false (`--debug-disable-reuse`) no match arm publishes a reuse token and no
// binding is admitted to the reference-counted heap on the strength of a reuse-safe rebuild, so
// every cell is allocated fresh and released by its owner as before reuse.
let lowerCoreProgramWithSourceAndReuse (reuseEnabled: Bool) (filePath: Str) (source: Str) (program: ProgramSyntax) =
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
                    |> withStateReuseEnabled(reuseEnabled))
                |> withProgramParameterOwnership(program)
                |> lowerProgramPasses(items)(trailingBody)(None)
                |> buildProgram

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
let lowerCoreProgramWithEnvironment (environment: TypeEnvironment) (program: ProgramSyntax) =
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
