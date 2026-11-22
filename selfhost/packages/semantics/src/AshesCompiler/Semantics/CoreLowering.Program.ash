// Whole-program lowering: top-level bindings and their uses, type, capability, external and trait
// registration, the lowering passes with their replay and deferred goals, and the program-wide
// ownership facts. A part of `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Frontend.Syntax.LetBindingSyntax
import AshesCompiler.Frontend.Syntax.TopLevelItem
import AshesCompiler.Frontend.Syntax.ProgramSyntax
import AshesCompiler.Frontend.Syntax.CapabilityDecl
import AshesCompiler.Frontend.Syntax.CapabilityOperation
import AshesCompiler.Frontend.Syntax.TypeDecl
import AshesCompiler.Frontend.Syntax.TypeAliasDecl
import AshesCompiler.Frontend.Syntax.ZeroCostTypeDecl
import AshesCompiler.Frontend.Syntax.ExternalDecl
import AshesCompiler.Frontend.Syntax.TraitMethodDecl
import AshesCompiler.Frontend.Syntax.TraitDecl
import AshesCompiler.Frontend.Syntax.ProvideBinding
import AshesCompiler.Frontend.Syntax.ProvideDecl
import AshesCompiler.Frontend.Syntax.ExportDecl
import AshesCompiler.Frontend.Syntax.ExportItem
import AshesCompiler.Frontend.Syntax.TraitImplementationDecl
import AshesCompiler.Frontend.Syntax.TraitImplementationMethodBinding
import AshesCompiler.Frontend.Syntax.TraitConstraintSyntax
import AshesCompiler.Frontend.Syntax.TypeConstructor
import AshesCompiler.Frontend.Syntax.TypeParameter
import AshesCompiler.Frontend.Syntax.TypeExpr
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.CallOwnership
import AshesCompiler.Semantics.CoreCapabilityLowering
import AshesCompiler.Semantics.CoreExternalLowering
import AshesCompiler.Semantics.DerivingExpansion.expandDerivedImplementations
import AshesCompiler.Semantics.TypeInference.addTraitImplementation
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.ExternalAbi.validateExternalProgramAbi
import AshesCompiler.Semantics.ExternalTyping
import AshesCompiler.Semantics.TypeResolution.addTypeAliasDefinition
import AshesCompiler.Semantics.TypeResolution.addExternalTypeDefinition
import AshesCompiler.Semantics.TypeResolution.addTypeDefinition
import AshesCompiler.Semantics.TypeResolution.addZeroCostTypeDefinitionWithProvenance
import AshesCompiler.Semantics.TypeResolution.DeclarationProvenance
import AshesCompiler.Semantics.TypeResolution.emptyTypeResolutionContext
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.ResultReachSummaries.ReachSummary
import AshesCompiler.Semantics.ResultReachSummaries.ReachFunction
import AshesCompiler.Semantics.ResultReachSummaries.buildReachRegistry
import AshesCompiler.Semantics.ResultReachSummaries.computeReachSummaries
import AshesCompiler.Semantics.ResultReachSummaries.summariesTable
import AshesCompiler.Semantics.ResultReachSummaries.expressionReachSummaries
import AshesCompiler.Semantics.OwnershipInference.inferProgramParameterOwnership
import AshesCompiler.Semantics.OwnershipInference.topLevelFunctions
import AshesCompiler.Semantics.OwnershipInference.programSoleNullaryConstructors
import AshesCompiler.Semantics.OwnershipInference.collectAllCallSites
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.TaglessAdtLayout
import AshesCompiler.Semantics.TraitEvidenceRewriting
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.PerceusLifetimePlacement
import AshesCompiler.Semantics.NodeIdentity.foldExpressionNodes
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
import AshesCompiler.Semantics.CoreLowering.TcoReset
import AshesCompiler.Semantics.CoreLowering.ResultHandOver
import AshesCompiler.Semantics.CoreLowering.Bindings
import AshesCompiler.Semantics.CoreLowering.LoopPlacement
import AshesCompiler.Semantics.CoreLowering.CallArguments
import AshesCompiler.Semantics.CoreLowering.Matches
import AshesCompiler.Semantics.CoreLowering.RecursiveGroups
import AshesCompiler.Semantics.CoreLowering.Operators
import AshesCompiler.Semantics.CoreLowering.Expressions
import AshesCompiler.Semantics.StrictPartialStages.scheduleStrictProgram
export (
    value failedCoreLowering,
    value allTopLevelBindingNames,
    value lowerProgramPasses,
    type CoreProgramPassMeasurement(..),
    value lowerProgramPassesMeasured,
    value lowerProgramPassesMeasuredWithProgress,
    value registeredProgramState,
    value buildProgram,
    value withProgramParameterOwnership,
    value withExpressionReachSummaries,
)

let entryOrigin =
    IrFunctionOrigin(
        generatedLabel = "_start_main",
        originKind = ProgramEntryOrigin,
        sourceOrigin = None,
        parentGeneratedLabel = None,
        compilerOwner = None,
        stableDiscriminator = None,
        generationLocation = None
    )

let hasFunctions functions =
    match functions with
        | [] -> false
        | _ -> true

let entryInstructions temp instructions =
    reverse(IrInstruction(
        instruction = Return(temp),
        location = None
    ) :: instructions)

let failedCoreLowering error =
    CoreLoweringResult(
        program = None,
        semanticType = SemNever,
        error = Some(error),
        valuePlacements = [],
        joinRepresentations = []
    )

type CoreProgramUses =
    | printInt: Bool
    | printStr: Bool
    | printBool: Bool
    | concatStr: Bool

let emptyCoreProgramUses =
    CoreProgramUses(
        printInt = false,
        printStr = false,
        printBool = false,
        concatStr = false
    )

let includeCoreInstruction operation (uses: CoreProgramUses) =
    match operation with
        | PrintInt(_) -> uses with printInt = true
        | PrintStr(_) -> uses with printStr = true
        | PrintBool(_) -> uses with printBool = true
        | ConcatStr(_, _, _, _) -> uses with concatStr = true
        | ConcatStrTip(_, _, _, _, _, _) -> uses with concatStr = true
        | _ -> uses

let recursive collectCoreInstructionUses instructions uses =
    match instructions with
        | [] -> uses
        | IrInstruction { instruction = operation } :: rest ->
            uses
            |> includeCoreInstruction(operation)
            |> collectCoreInstructionUses(rest)

let recursive collectCoreFunctionUses functions uses =
    match functions with
        | [] -> uses
        | IrFunction { instructions = instructions } :: rest ->
            uses
            |> collectCoreInstructionUses(instructions)
            |> collectCoreFunctionUses(rest)

// The names declared so far, as a map so a program's thousands of declarations are each checked in
// logarithmic time.
type TopLevelDuplicateCheck =
    | seen: MapTree(Str, Bool)
    | duplicate: Maybe(Str)

let recursive checkTopLevelNames (names: List(Str)) (seen: MapTree(Str, Bool)) =
    match names with
        | [] -> TopLevelDuplicateCheck(seen = seen, duplicate = None)
        | name :: rest ->
            match Ashes.Collection.Map.getStr(name)(seen) with
                | Some(_declared) -> TopLevelDuplicateCheck(seen = seen, duplicate = Some(name))
                | None ->
                    seen
                    |> Ashes.Collection.Map.setStr(name)(true)
                    |> checkTopLevelNames(rest)

let recursive letBindingSyntaxPairs bindings state =
    match bindings with
        | [] -> []
        | LetBindingSyntax { name = name, value = value, typeAnnotation = annotation } :: rest -> (name, bindingLambdaValue(annotation)(value)(state)) :: letBindingSyntaxPairs(rest)(state)

let recursive recursiveMemberStartsOf (groupStart: Int) (bindings: List(LetBindingSyntax)) =
    match bindings with
        | [] -> []
        | LetBindingSyntax { name = name, declarationStart = start } :: rest ->
            if start >= 0
            then (name, start, groupStart) :: recursiveMemberStartsOf(groupStart)(rest)
            else recursiveMemberStartsOf(groupStart)(rest)

let groupMemberStarts (bindings: List(LetBindingSyntax)) (state: CoreLoweringState) =
    match stateRecursiveDeclarationSpan(state) with
        | Some(TextSpan(start, _end)) ->
            withStateRecursiveMemberStarts(recursiveMemberStartsOf(start)(bindings))(state)
        | None -> state

let recursive letBindingSyntaxNames bindings =
    match bindings with
        | [] -> []
        | LetBindingSyntax { name = name } :: rest -> name :: letBindingSyntaxNames(rest)

// Every top-level value-binding name in the WHOLE program (not just the items seen so far during
// lowerCoreProgramItems's sequential scan) — computed once, up front, at each whole-program entry
// point, so lowerCoreOrdinaryVariable's final "unknown binding" fallback can distinguish a genuine
// undefined identifier from a Model-A forward reference (a name that IS declared, just later in
// the file). Mirrors stage-0's CollectTopLevelBindingNames/_topLevelBindingNames
// (Lowering.TopLevel.cs/Lowering.cs) and the same LowerVarUnbound-style specialization
// (Lowering.cs:2844).
let recursive allTopLevelBindingNames (items: List(TopLevelItem)) (names: MapTree(Str, Bool)) =
    match items with
        | [] -> names
        | TopLevelAt(_span, inner) :: rest -> allTopLevelBindingNames(inner :: rest)(names)
        | TopLevelLet(LetBindingSyntax { name = name }, _isRecursive) :: rest ->
            names
            |> Ashes.Collection.Map.setStr(name)(true)
            |> allTopLevelBindingNames(rest)
        | TopLevelRecursiveGroup(bindings) :: rest ->
            names
            |> addTopLevelNames(letBindingSyntaxNames(bindings))
            |> allTopLevelBindingNames(rest)
        | _ :: rest -> allTopLevelBindingNames(rest)(names)
and addTopLevelNames (added: List(Str)) (names: MapTree(Str, Bool)) =
    match added with
        | [] -> names
        | name :: rest ->
            names
            |> Ashes.Collection.Map.setStr(name)(true)
            |> addTopLevelNames(rest)

// When an inference environment is available, elaborates a constrained top-level binding's value
// into ordinary syntax with hidden dictionary parameters and forwards evidence at any call site
// inside it that reaches another constrained top-level binding
// (rewriteTraitConstrainedTopLevelValue, AshesCompiler.Semantics.TraitEvidenceRewriting); a binding
// with no constraints, or no environment supplied at all (the existing no-environment entry
// points), lowers unchanged. A forwarding failure (no active dictionary supplies the callee's
// required evidence) surfaces as UnresolvedTraitEvidenceForwarding at the two call sites below,
// rather than silently emitting a call the callee's hidden parameter can't be supplied for.
// Brackets one flat top-level let with the arena save/restore/reclaim triple stage 0 always emits
// (SaveArenaState before the value, RestoreArenaState + ReclaimArenaChunks after the rest of the
// program), the first slice of Perceus/region lifetime placement ported to the self-hosted
// lowerer. Only reached once the whole remaining top-level sequence has been proven, by
// topLevelItemsProvablyArenaSafe, to contain no heap value that could cross the restore boundary —
// the general case additionally needs a CopyOutArena for an escaping heap result, not yet ported
// (see docs/md/future/SELF_HOSTING.md).
// The rest of the program is lowered between opening a declaration's bracket and closing it, so the
// flat chain is walked in two halves: `openArenaBracketedTopLevelLet` opens the bracket, lowers the
// value and stores it, and records the close still owed as a PendingTopLevelClose;
// lowerCoreProgramItems then goes on to the next declaration with that record pushed, and
// closePendingTopLevelItems closes every record innermost first once the trailing body is lowered.
// A chain of thousands of declarations thereby costs a list cell each rather than a stack frame
// each, the way stage 0's sequential binding chain is lowered. Every later declaration sets its
// own currentSpan without restoring this one, so a close records the declaration's own span and
// closes under it, keeping a chain of top-level declarations stepping forwards in a debugger the
// way the nested form does through its body's ExprAt.
type PendingTopLevelClose =
    | closeOwnedTypeName: Maybe(Str)
    | closeOwnerSlot: Int
    | closeCursorSlot: Int
    | closeEndSlot: Int
    | closeSpan: Maybe(TextSpan)
    | closeOuterBindings: List(CoreBinding)
    | closeBracket: Bool

// A recursive group's close: only its names are taken back out of scope.
let pendingBindingsRestore (outerBindings: List(CoreBinding)) =
    PendingTopLevelClose(
        closeOwnedTypeName = None,
        closeOwnerSlot = -1,
        closeCursorSlot = -1,
        closeEndSlot = -1,
        closeSpan = None,
        closeOuterBindings = outerBindings,
        closeBracket = false
    )

type OpenedTopLevelChain =
    | chainState: CoreLoweringState
    | chainPending: List(PendingTopLevelClose)
    | chainError: Maybe(CoreLoweringError)

let openedChainFailure (pending: List(PendingTopLevelClose)) (error: CoreLoweringError) (state: CoreLoweringState) = OpenedTopLevelChain(chainState = state, chainPending = pending, chainError = Some(error))

// `remainingBody` is the rest of the program the declaration binds over, read only for the
// value's request (a look at its immediate shape); the two facts that need the whole chain walked
// come precomputed from the program analysis: `escapeBody`, the innermost body the chain escapes
// through (escapingChainBody), and `tailForwarded`, whether the chain's result forwards this
// binding (isTailForwardedBindingResult).
let registerConstrainedLogicalReference name scheme state =
    match state
    |> stateTopLevelFunctionRefIndex
    |> lookupTopLevelFunctionRef(name) with
        | Some((label, _physical)) -> withRegisteredTopLevelFunctionRef(name)(label)(scheme)(state)
        | None -> state

let openStoredConstrainedTopLevel name value provisional scheme cursorSlot endSlot (saved: CoreLoweringState) escapeBody tailForwarded outerBindings pending loweredValue =
    match loweredValue with
        | LoweredCoreValue { state = failed, error = Some(error) } -> openedChainFailure(pending)(error)(failed)
        | LoweredCoreValue { error = None } ->
            match loweredValue
            |> withLoweredConsumerRequest(consumerRequestOf(saved))
            |> armOwnerReleasePlan(value)
            |> storeLetValueInSlot(name)(value)(tailForwarded)(escapeBodyRequest(escapeBody)(consumerRequestOf(saved))(saved))(outerBindings) with
                | (LoweredCoreValue { state = failed, error = Some(error) }, _slot) -> openedChainFailure(pending)(error)(failed)
                | (LoweredCoreValue { state = stored, error = None }, ownerSlot) ->
                    let scheme = storedConstrainedScheme(provisional)(scheme)(outerBindings)(stored)
                    in
                        OpenedTopLevelChain(chainState = stored
                        |> restoreBindings(replaceConstrainedScheme(name)(scheme)(stored.bindings))
                        |> registerConstrainedLogicalReference(name)(scheme), chainPending = PendingTopLevelClose(closeOwnedTypeName = letOwnedTypeName(value)(loweredValue)(saved), closeOwnerSlot = ownerSlot, closeCursorSlot = cursorSlot, closeEndSlot = endSlot, closeSpan = saved.currentSpan, closeOuterBindings = outerBindings, closeBracket = true) :: pending, chainError = None)

let tryOpenConstrainedTopLevel name value annotation requirements escapeBody tailForwarded outerBindings pending state =
    match constrainedAliasBinding(value)(state) with
        | Some(binding) ->
            match openArenaBracket(state) with
                | ArenaBracket { bracketState = saved, bracketCursorSlot = cursor, bracketEndSlot = end } ->
                    saved
                    |> lowerConstrainedAlias(binding)
                    |> openStoredConstrainedTopLevel(name)(value)(false)(binding.scheme)(cursor)(end)(saved)(escapeBody)(tailForwarded)(outerBindings)(pending)
                    |> Some
        | None ->
            match constrainedBindingPlan(name)(false)(value)(annotation)(requirements)(state) with
                | None -> None
                | Some(plan) ->
                    match openArenaBracket(state) with
                        | ArenaBracket { bracketState = saved, bracketCursorSlot = cursor, bracketEndSlot = end } ->
                            saved
                            |> lowerConstrainedBindingValue(name)(plan)(value)(lowerCore)
                            |> openStoredConstrainedTopLevel(name)(value)(true)(plan.scheme)(cursor)(end)(saved)(escapeBody)(tailForwarded)(outerBindings)(pending)
                            |> Some

let openUnconstrainedArenaBracketedTopLevelLet name value annotation remainingBody (escapeBody: Maybe(Expr)) (tailForwarded: Bool) environment outerBindings stackClosure (pending: List(PendingTopLevelClose)) (state: CoreLoweringState) =
    match openArenaBracket(state) with
        | ArenaBracket { bracketState = saved, bracketCursorSlot = cursorSlot, bracketEndSlot = endSlot } ->
            match rewriteTraitConstrainedTopLevelValue(name)(bindingLambdaValue(annotation)(value)(state))(environment) with
                | TraitConstrainedTopLevelValueRewriting { value = _rewrittenValue, error = Some(error) } -> openedChainFailure(pending)(UnresolvedTraitEvidenceForwarding(error))(saved)
                | TraitConstrainedTopLevelValueRewriting { value = rewrittenValue, error = None } ->
                    match saved
                    |> withConsumerRequest(letValueRequest(name)(rewrittenValue)(remainingBody)(saved))
                    |> armSourceFunction(name)(rewrittenValue)(stackClosure)
                    |> lowerCore(rewrittenValue)
                    |> bindLoweredAnnotation(rewrittenValue)(annotatedValueType(annotation)(rewrittenValue)(saved)) with
                        | LoweredCoreValue { state = failedState, error = Some(error) } -> openedChainFailure(pending)(error)(failedState)
                        | LoweredCoreValue { error = None } as loweredValue ->
                            match loweredValue
                            |> withLoweredConsumerRequest(consumerRequestOf(saved))
                            |> armOwnerReleasePlan(rewrittenValue)
                            |> storeLetValueInSlot(name)(rewrittenValue)(tailForwarded)(escapeBodyRequest(escapeBody)(consumerRequestOf(saved))(saved))(outerBindings) with
                                | (LoweredCoreValue { state = failedState, error = Some(error) }, _slot) -> openedChainFailure(pending)(error)(failedState)
                                | (LoweredCoreValue { state = stored, error = None }, ownerSlot) ->
                                    OpenedTopLevelChain(
                                        chainState = stored,
                                        chainPending = PendingTopLevelClose(
                                            closeOwnedTypeName = letOwnedTypeName(rewrittenValue)(loweredValue)(saved),
                                            closeOwnerSlot = ownerSlot,
                                            closeCursorSlot = cursorSlot,
                                            closeEndSlot = endSlot,
                                            closeSpan = state.currentSpan,
                                            closeOuterBindings = outerBindings,
                                            closeBracket = true
                                        ) :: pending,
                                        chainError = None
                                    )

// Closes the opened top-level declarations innermost first around the lowered trailing body: a
// bracketed let releases its owner and restores the arena under the declaration's own span, a
// recursive group only takes its names back out of scope. A failure passes through with each
// declaration's names taken out of scope, as the nested form's failure did.
let openUnregisteredArenaTopLevelLet name value annotation requirements remainingBody escapeBody tailForwarded environment outerBindings stackClosure pending state =
    match tryOpenConstrainedTopLevel(name)(value)(annotation)(requirements)(escapeBody)(tailForwarded)(outerBindings)(pending)(state) with
        | Some(opened) -> opened
        | None -> openUnconstrainedArenaBracketedTopLevelLet(name)(value)(annotation)(remainingBody)(escapeBody)(tailForwarded)(environment)(outerBindings)(stackClosure)(pending)(state)

let openArenaBracketedTopLevelLet name value annotation requirements remainingBody escapeBody tailForwarded environment outerBindings stackClosure pending state =
    match openUnregisteredArenaTopLevelLet(name)(value)(annotation)(requirements)(remainingBody)(escapeBody)(tailForwarded)(environment)(outerBindings)(stackClosure)(pending)(state) with
        | OpenedTopLevelChain { chainState = opened, chainError = None } as result -> result with chainState = registerConcreteSource(name)(value)(false)(opened)
        | failed -> failed

let recursive closePendingTopLevelItems (pending: List(PendingTopLevelClose)) (lowered: LoweredCoreValue) =
    match (pending, lowered) with
        | ([], _lowered) -> lowered
        | (close :: rest, LoweredCoreValue { state = failedState, error = Some(_error) }) -> closePendingTopLevelItems(rest)((lowered with state = restoreBindings(close.closeOuterBindings)(failedState)))
        | (close :: rest, LoweredCoreValue { state = bodyState, temp = resultTemp, semanticType = resultType, error = None }) ->
            if close.closeBracket
            then
                match closeOwnedLetBracket(close.closeOwnedTypeName)(close.closeOwnerSlot)(close.closeCursorSlot)(close.closeEndSlot)(resultTemp)(resultType)((bodyState with bindings = close.closeOuterBindings, currentSpan = close.closeSpan)) with
                    | (closed, finalTemp) ->
                        closed
                        |> finishClosedLetResult(finalTemp)(resultType)
                        |> closePendingTopLevelItems(rest)
            else
                bodyState
                |> restoreBindings(close.closeOuterBindings)
                |> success(resultTemp)(resultType)
                |> closePendingTopLevelItems(rest)

// A single, non-cascading `RcDrop` fires for a top-level `let` whose value is a direct,
// fully-saturated call to a known field-carrying constructor (see
// `directSingleArgRcConstructorLayout` below) and whose name is never referenced again by the rest
// of the program. Two independent checks gate this, both biased toward "leave it alone" whenever
// unsure — a missed drop leaks; an incorrect one is a use-after-free:
//
// 1. `exprMayReferenceName` matches every one of `Expr`'s constructors explicitly, with no wildcard
//    case — the compiler rejects this file if a new `Expr` variant is ever left unhandled. It does
//    not reason about name shadowing (an inner `let`/lambda/pattern rebinding the same name): it
//    always recurses into every subexpression regardless, which only ever under-counts dead names,
//    never mistakes a live one for dead.
// 2. `directSingleArgRcConstructorLayout` only recognizes one, fully-saturating argument
//    (`Ctor(arg)` — every constructor `standardConstructorLayouts` registers is exactly this
//    shape). A zero-cost constructor, a multi-argument/curried constructor, or a partial
//    application (a closure value, not an allocated cell — dropping it as a raw ADT pointer would
//    be a type confusion) all answer `None`.
//
// Every constructor reachable here wraps one plain scalar field, so `RcDrop`'s
// `structuralDropperLabel` is always `None` — a field that is itself RC-managed and needs its own
// release before this cell's header is freed is not handled.
let recursive exprMayReferenceName (expr: Expr) (name: Str) =
    match expr with
        | ExprAt(_span, inner) -> exprMayReferenceName(inner)(name)
        | ExprNode(_node, inner) -> exprMayReferenceName(inner)(name)
        | ExprNode(_id, inner) -> exprMayReferenceName(inner)(name)
        | ExprInt(_value) -> false
        | ExprBigInt(_value) -> false
        | ExprUInt(_value, _bitWidth, _suffix) -> false
        | ExprFloat(_value, _suffix) -> false
        | ExprString(_value) -> false
        | ExprRune(_value) -> false
        | ExprBool(_value) -> false
        | ExprVar(varName) -> varName == name
        | ExprQualifiedVar(_moduleName, _memberName) -> false
        | ExprAdd(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprSubtract(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprMultiply(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprDivide(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprModulo(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprBitwiseAnd(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprBitwiseOr(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprBitwiseXor(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprShiftLeft(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprShiftRight(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprBitwiseNot(operand) -> exprMayReferenceName(operand)(name)
        | ExprLogicalNot(operand) -> exprMayReferenceName(operand)(name)
        | ExprLogicalAnd(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprLogicalOr(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprGreaterThan(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprLessThan(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprGreaterOrEqual(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprLessOrEqual(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprEqual(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprNotEqual(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprResultPipe(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprResultMapErrorPipe(left, right) -> exprOrMayReferenceName(left)(right)(name)
        | ExprLet(_boundName, letValue, letBody, _params, _ann, _traits) -> exprOrMayReferenceName(letValue)(letBody)(name)
        | ExprLetResult(_boundName, letValue, letBody) -> exprOrMayReferenceName(letValue)(letBody)(name)
        | ExprLetRecursive(_boundName, letValue, letBody, _params, _ann, _traits) -> exprOrMayReferenceName(letValue)(letBody)(name)
        | ExprIf(cond, thenBranch, elseBranch) ->
            if exprMayReferenceName(cond)(name)
            then true
            else exprOrMayReferenceName(thenBranch)(elseBranch)(name)
        | ExprLambda(_param, body, _ann) -> exprMayReferenceName(body)(name)
        | ExprCall(func, arg, _isSugar, _layout) -> exprOrMayReferenceName(func)(arg)(name)
        | ExprTuple(elements) -> exprListMayReferenceName(elements)(name)
        | ExprList(elements, _isMultiline) -> exprListMayReferenceName(elements)(name)
        | ExprCons(head, tail) -> exprOrMayReferenceName(head)(tail)(name)
        | ExprMatch(scrutinee, arms, _defaultArm) ->
            if exprMayReferenceName(scrutinee)(name)
            then true
            else exprMatchArmsMayReferenceName(arms)(name)
        | ExprAwait(inner) -> exprMayReferenceName(inner)(name)
        | ExprRecord(_typeName, fields, _isMultiline) -> exprFieldsMayReferenceName(fields)(name)
        | ExprRecordUpdate(record, fields) ->
            if exprMayReferenceName(record)(name)
            then true
            else exprFieldsMayReferenceName(fields)(name)
        | ExprPerform(inner) -> exprMayReferenceName(inner)(name)
        | ExprHandle(inner, arms) ->
            if exprMayReferenceName(inner)(name)
            then true
            else exprHandleArmsMayReferenceName(arms)(name)
and exprOrMayReferenceName left right name =
    if exprMayReferenceName(left)(name)
    then true
    else exprMayReferenceName(right)(name)
and exprListMayReferenceName (list: List(Expr)) (name: Str) =
    match list with
        | [] -> false
        | head :: tail ->
            if exprMayReferenceName(head)(name)
            then true
            else exprListMayReferenceName(tail)(name)
and exprFieldsMayReferenceName (fields: List((Str, Expr))) (name: Str) =
    match fields with
        | [] -> false
        | (_fieldName, fieldExpr) :: tail ->
            if exprMayReferenceName(fieldExpr)(name)
            then true
            else exprFieldsMayReferenceName(tail)(name)
and exprMatchArmsMayReferenceName (arms: List((Pattern, Expr, Maybe(Expr)))) (name: Str) =
    match arms with
        | [] -> false
        | (_pattern, body, guard) :: tail ->
            let guardMayReference =
                match guard with
                    | None -> false
                    | Some(guardExpr) -> exprMayReferenceName(guardExpr)(name)
            in
                if guardMayReference
                then true
                else
                    if exprMayReferenceName(body)(name)
                    then true
                    else exprMatchArmsMayReferenceName(tail)(name)
and exprHandleArmsMayReferenceName (arms: List((Maybe(Str), Str, List(Pattern), Expr))) (name: Str) =
    match arms with
        | [] -> false
        | (_resumeName, _operationName, _patterns, body) :: tail ->
            if exprMayReferenceName(body)(name)
            then true
            else exprHandleArmsMayReferenceName(tail)(name)

let recursive letBindingSyntaxListMayReferenceName (bindings: List(LetBindingSyntax)) (name: Str) =
    match bindings with
        | [] -> false
        | LetBindingSyntax { value = value } :: rest ->
            if exprMayReferenceName(value)(name)
            then true
            else letBindingSyntaxListMayReferenceName(rest)(name)

// As `exprMayReferenceName`, but over the flat top-level `let`/trailing-expression sequence
// `lowerCoreProgramItems` walks (Model A), matching `topLevelItemsProvablyArenaSafe`'s own shape.
// Conservative the same way: any item this isn't specifically taught about (a recursive group, a
// self-recursive let) answers `true` via its value/binding expressions rather than trying to reason
// about what it could shadow or capture.
// Stage 0's direct-callee analysis asked once per program rather than once per binding over the
// rest of the program: the free names every top-level value and the trailing expression use
// other than as the callee of an application, collected in one walk. A binder ends the walk for
// the name it binds within its scope, and an expression form the walk does not know marks every
// name, the way `nameHasNonCalleeUse` answers for one name. Top-level names are unique
// (`checkTopLevelNames`), so a plain binding's verdict is its name's absence here.
type NonCalleeUses =
    | names: MapTree(Str, Bool)
    | everyName: Bool
    | includeCallees: Bool

let recordNonCalleeUse (candidate: Str) (shadowed: List(Str)) (uses: NonCalleeUses) =
    if containsName(candidate)(shadowed)
    then uses
    else uses with names = Ashes.Collection.Map.setStr(candidate)(true)(uses.names)

let recursive patternBoundNames (pattern: Pattern) (names: List(Str)) =
    match pattern with
        | PatternAt(_span, inner) -> patternBoundNames(inner)(names)
        | PatternVar(candidate) -> candidate :: names
        | PatternCons(head, tail) ->
            names
            |> patternBoundNames(head)
            |> patternBoundNames(tail)
        | PatternTuple(elements) -> patternsBoundNames(elements)(names)
        | PatternConstructor(_constructor, arguments) -> patternsBoundNames(arguments)(names)
        | PatternRecord(_typeName, fields) -> fieldPatternsBoundNames(fields)(names)
        | PatternAs(inner, alias) -> patternBoundNames(inner)(alias :: names)
        | PatternOr(alternatives) -> patternsBoundNames(alternatives)(names)
        | _ -> names
and patternsBoundNames (patterns: List(Pattern)) (names: List(Str)) =
    match patterns with
        | [] -> names
        | pattern :: rest ->
            names
            |> patternBoundNames(pattern)
            |> patternsBoundNames(rest)
and fieldPatternsBoundNames (fields: List((Str, Pattern))) (names: List(Str)) =
    match fields with
        | [] -> names
        | (_field, pattern) :: rest ->
            names
            |> patternBoundNames(pattern)
            |> fieldPatternsBoundNames(rest)

let recursive collectNonCalleeUses (expr: Expr) (asCallee: Bool) (shadowed: List(Str)) (uses: NonCalleeUses) =
    match expr with
        | ExprAt(_span, inner) -> collectNonCalleeUses(inner)(asCallee)(shadowed)(uses)
        | ExprNode(_node, inner) -> collectNonCalleeUses(inner)(asCallee)(shadowed)(uses)
        | ExprInt(_value) -> uses
        | ExprBigInt(_value) -> uses
        | ExprUInt(_value, _bitWidth, _suffix) -> uses
        | ExprFloat(_value, _suffix) -> uses
        | ExprString(_value) -> uses
        | ExprRune(_value) -> uses
        | ExprBool(_value) -> uses
        | ExprVar(candidate) ->
            if asCallee && !uses.includeCallees
            then uses
            else recordNonCalleeUse(candidate)(shadowed)(uses)
        | ExprQualifiedVar(owner, _memberName) ->
            if uses.includeCallees
            then recordNonCalleeUse(owner)(shadowed)(uses)
            else uses
        | ExprCall(function, argument, _isSugar, _layout) ->
            uses
            |> collectNonCalleeUses(function)(true)(shadowed)
            |> collectNonCalleeUses(argument)(false)(shadowed)
        | ExprLambda(parameter, body, _annotation) -> collectNonCalleeUses(body)(false)(parameter :: shadowed)(uses)
        | ExprLet(bound, value, body, _parameters, _annotation, _requirements) ->
            uses
            |> collectNonCalleeUses(value)(false)(shadowed)
            |> collectNonCalleeUses(body)(false)(bound :: shadowed)
        | ExprLetResult(bound, value, body) ->
            uses
            |> collectNonCalleeUses(value)(false)(shadowed)
            |> collectNonCalleeUses(body)(false)(bound :: shadowed)
        | ExprLetRecursive(bound, value, body, _parameters, _annotation, _requirements) ->
            uses
            |> collectNonCalleeUses(value)(false)(bound :: shadowed)
            |> collectNonCalleeUses(body)(false)(bound :: shadowed)
        | ExprIf(condition, thenBranch, elseBranch) ->
            uses
            |> collectNonCalleeUses(condition)(false)(shadowed)
            |> collectNonCalleeUses(thenBranch)(false)(shadowed)
            |> collectNonCalleeUses(elseBranch)(false)(shadowed)
        | ExprAdd(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprSubtract(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprMultiply(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprDivide(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprModulo(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprBitwiseAnd(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprBitwiseOr(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprBitwiseXor(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprShiftLeft(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprShiftRight(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprLogicalAnd(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprLogicalOr(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprGreaterThan(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprLessThan(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprGreaterOrEqual(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprLessOrEqual(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprEqual(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprNotEqual(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprResultPipe(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprResultMapErrorPipe(left, right) -> collectBothNonCalleeUses(left)(right)(shadowed)(uses)
        | ExprCons(head, tail) -> collectBothNonCalleeUses(head)(tail)(shadowed)(uses)
        | ExprBitwiseNot(operand) -> collectNonCalleeUses(operand)(false)(shadowed)(uses)
        | ExprLogicalNot(operand) -> collectNonCalleeUses(operand)(false)(shadowed)(uses)
        | ExprTuple(elements) -> collectAllNonCalleeUses(elements)(shadowed)(uses)
        | ExprList(elements, _isMultiline) -> collectAllNonCalleeUses(elements)(shadowed)(uses)
        | ExprRecord(_typeName, fields, _isMultiline) -> collectFieldNonCalleeUses(fields)(shadowed)(uses)
        | ExprRecordUpdate(record, fields) ->
            uses
            |> collectNonCalleeUses(record)(false)(shadowed)
            |> collectFieldNonCalleeUses(fields)(shadowed)
        | ExprMatch(scrutinee, arms, _position) ->
            uses
            |> collectNonCalleeUses(scrutinee)(false)(shadowed)
            |> collectArmNonCalleeUses(arms)(shadowed)
        | _ -> uses with everyName = true
and collectBothNonCalleeUses (left: Expr) (right: Expr) (shadowed: List(Str)) (uses: NonCalleeUses) =
    uses
    |> collectNonCalleeUses(left)(false)(shadowed)
    |> collectNonCalleeUses(right)(false)(shadowed)
and collectAllNonCalleeUses (expressions: List(Expr)) (shadowed: List(Str)) (uses: NonCalleeUses) =
    match expressions with
        | [] -> uses
        | expression :: rest ->
            uses
            |> collectNonCalleeUses(expression)(false)(shadowed)
            |> collectAllNonCalleeUses(rest)(shadowed)
and collectFieldNonCalleeUses (fields: List((Str, Expr))) (shadowed: List(Str)) (uses: NonCalleeUses) =
    match fields with
        | [] -> uses
        | (_field, expression) :: rest ->
            uses
            |> collectNonCalleeUses(expression)(false)(shadowed)
            |> collectFieldNonCalleeUses(rest)(shadowed)
and collectArmNonCalleeUses (arms: List((Pattern, Expr, Maybe(Expr)))) (shadowed: List(Str)) (uses: NonCalleeUses) =
    match arms with
        | [] -> uses
        | (pattern, body, guard) :: rest ->
            let armShadowed = patternBoundNames(pattern)(shadowed)
            in
                uses
                |> collectGuardNonCalleeUses(guard)(armShadowed)
                |> collectNonCalleeUses(body)(false)(armShadowed)
                |> collectArmNonCalleeUses(rest)(shadowed)
and collectGuardNonCalleeUses (guard: Maybe(Expr)) (shadowed: List(Str)) (uses: NonCalleeUses) =
    match guard with
        | None -> uses
        | Some(condition) -> collectNonCalleeUses(condition)(false)(shadowed)(uses)

let recursive letBindingValuesNonCalleeUses (bindings: List(LetBindingSyntax)) (uses: NonCalleeUses) =
    match bindings with
        | [] -> uses
        | LetBindingSyntax { value = value } :: rest ->
            uses
            |> collectNonCalleeUses(value)(false)([])
            |> letBindingValuesNonCalleeUses(rest)

// The all-reference mode also includes bodies lowered through registered evidence
// and providers, rather than only ordinary value bindings. The ownership analysis
// retains its original non-callee-only mode and decisions.
let recursive implementationReferenceUses bindings uses =
    match bindings with
        | [] -> uses
        | TraitImplementationMethodBinding { implementation = body } :: rest ->
            uses
            |> collectNonCalleeUses(body)(false)([])
            |> implementationReferenceUses(rest)

let recursive defaultMethodReferenceUses methods uses =
    match methods with
        | [] -> uses
        | TraitMethodDecl { defaultImplementation = None } :: rest -> defaultMethodReferenceUses(rest)(uses)
        | TraitMethodDecl { defaultImplementation = Some(body) } :: rest ->
            uses
            |> collectNonCalleeUses(body)(false)([])
            |> defaultMethodReferenceUses(rest)

let recursive providerReferenceUses bindings uses =
    match bindings with
        | [] -> uses
        | ProvideBinding { implementation = body } :: rest ->
            uses
            |> collectNonCalleeUses(body)(false)([])
            |> providerReferenceUses(rest)

let recursive exportReferenceUses items uses =
    match items with
        | [] -> uses
        | ExportValue(name) :: rest ->
            uses
            |> recordNonCalleeUse(name)([])
            |> exportReferenceUses(rest)
        | ExportType(_name, _constructors) :: rest -> exportReferenceUses(rest)(uses)
        | ExportModule(_name) :: _rest -> uses with everyName = true

let recursive programNonCalleeUses (items: List(TopLevelItem)) (trailingBody: Expr) (uses: NonCalleeUses) =
    match items with
        | [] -> collectNonCalleeUses(trailingBody)(false)([])(uses)
        | TopLevelAt(_span, inner) :: rest -> programNonCalleeUses(inner :: rest)(trailingBody)(uses)
        | TopLevelLet(LetBindingSyntax { value = value }, _isRecursive) :: rest ->
            uses
            |> collectNonCalleeUses(value)(false)([])
            |> programNonCalleeUses(rest)(trailingBody)
        | TopLevelRecursiveGroup(bindings) :: rest ->
            uses
            |> letBindingValuesNonCalleeUses(bindings)
            |> programNonCalleeUses(rest)(trailingBody)
        | TopLevelImplementation(TraitImplementationDecl { bindings = bindings }) :: rest ->
            programNonCalleeUses(rest)(trailingBody)(if uses.includeCallees
            then implementationReferenceUses(bindings)(uses)
            else uses)
        | TopLevelTrait(TraitDecl { methods = methods }) :: rest ->
            programNonCalleeUses(rest)(trailingBody)(if uses.includeCallees
            then defaultMethodReferenceUses(methods)(uses)
            else uses)
        | TopLevelProvide(ProvideDecl { bindings = bindings }) :: rest ->
            programNonCalleeUses(rest)(trailingBody)(if uses.includeCallees
            then providerReferenceUses(bindings)(uses)
            else uses)
        | TopLevelExport(ExportDecl { items = exported }) :: rest ->
            programNonCalleeUses(rest)(trailingBody)(if uses.includeCallees
            then exportReferenceUses(exported)(uses)
            else uses)
        | _other :: rest -> programNonCalleeUses(rest)(trailingBody)(uses)

// A plain top-level binding's lambda value is a stack closure when nothing after it uses the
// name other than as a callee (`topLevelNameUsedOnlyAsDirectCallee` walked the rest of the
// program per binding for the same answer).
let topLevelNameOnlyDirectCallee (name: Str) (uses: NonCalleeUses) =
    if uses.everyName
    then false
    else
        match Ashes.Collection.Map.getStr(name)(uses.names) with
            | Some(_used) -> false
            | None -> true

// What the top-level item loop reads per item without walking the rest of the program again:
// the direct-callee verdicts and the nested body of the items still ahead, built once and
// peeled one `let` at a time.
// `escapeOfChain` is escapingChainBody's answer for the whole remaining chain: the chain's last
// binding, not the current one, decides it, so it holds for every declaration that still has a
// later binding after it, and the last declaration reads `escapeOfTrailing`, the answer for the
// trailing expression alone. `trailingFinalVar` is the bare variable the trailing expression's
// own chain ends in, when it ends in one; a chain ending in anything else forwards no binding.
// Together they answer, per declaration, the two questions that would otherwise walk the whole
// remaining chain again for each declaration.
type TopLevelLoweringAnalysis =
    | nonCalleeUses: NonCalleeUses
    | remainingBody: Expr
    | escapeOfChain: Maybe(Expr)
    | escapeOfTrailing: Maybe(Expr)
    | trailingFinalVar: Maybe(Str)

let recursive peelRemainingBody (count: Int) (body: Expr) =
    if count <= 0
    then body
    else
        match body with
            | ExprLet(_name, _value, next, _parameters, _annotation, _requirements) -> peelRemainingBody(count - 1)(next)
            | ExprLetRecursive(_name, _value, next, _parameters, _annotation, _requirements) -> peelRemainingBody(count - 1)(next)
            | _ -> body

let peelAnalysis (count: Int) (analysis: TopLevelLoweringAnalysis) = analysis with remainingBody = peelRemainingBody(count)(analysis.remainingBody)

// The rest of the program as the body a top-level `let` binds over, the way stage 0 nests every
// later item under it: each later `let` becomes a nested `let` (a recursive binding or group a
// `let recursive`) around the trailing expression, and the other items contribute nothing a
// `let` value's request reads.
let recursive remainingProgramBody (items: List(TopLevelItem)) (trailingBody: Expr) =
    match items with
        | [] -> trailingBody
        | TopLevelAt(_span, inner) :: rest -> remainingProgramBody(inner :: rest)(trailingBody)
        | TopLevelLet(LetBindingSyntax { name = name, value = value }, false) :: rest ->
            ExprLet(name)(value)(remainingProgramBody(rest)(trailingBody))([])(None)([])
        | TopLevelLet(LetBindingSyntax { name = name, value = value }, true) :: rest ->
            ExprLetRecursive(name)(value)(remainingProgramBody(rest)(trailingBody))([])(None)([])
        | TopLevelRecursiveGroup(bindings) :: rest ->
            trailingBody
            |> remainingProgramBody(rest)
            |> recursiveGroupBody(bindings)
        | _other :: rest -> remainingProgramBody(rest)(trailingBody)
and recursiveGroupBody (bindings: List(LetBindingSyntax)) (body: Expr) =
    match bindings with
        | [] -> body
        | LetBindingSyntax { name = name, value = value } :: rest ->
            ExprLetRecursive(name)(value)(recursiveGroupBody(rest)(body))([])(None)([])

// The bare variable a `let` chain's innermost body is, spans looked through.
let recursive chainFinalVar (body: Expr) =
    match body with
        | ExprAt(_span, inner) -> chainFinalVar(inner)
        | ExprNode(_node, inner) -> chainFinalVar(inner)
        | ExprLet(_name, _value, nested, _parameters, _annotation, _requirements) -> chainFinalVar(nested)
        | ExprLetRecursive(_name, _value, nested, _parameters, _annotation, _requirements) -> chainFinalVar(nested)
        | ExprVar(name) -> Some(name)
        | _ -> None

let programLoweringAnalysis (items: List(TopLevelItem)) (trailingBody: Expr) =
    (let remainingBody = remainingProgramBody(items)(trailingBody)
    in
        TopLevelLoweringAnalysis(
            nonCalleeUses = programNonCalleeUses(items)(trailingBody)(NonCalleeUses(names = Ashes.Collection.Map.empty, everyName = false, includeCallees = false)),
            remainingBody = remainingBody,
            escapeOfChain = escapingChainBody(remainingBody)(true),
            escapeOfTrailing = escapingChainBody(trailingBody)(true),
            trailingFinalVar = chainFinalVar(trailingBody)
        ))

// escapingChainBody's answer for the current remaining body: the chain's answer while a later
// binding remains, the trailing expression's own once none does.
let topLevelEscapeBody (analysis: TopLevelLoweringAnalysis) =
    match analysis.remainingBody with
        | ExprLet(_name, _value, _nested, _parameters, _annotation, _requirements) -> analysis.escapeOfChain
        | ExprLetRecursive(_name, _value, _nested, _parameters, _annotation, _requirements) -> analysis.escapeOfChain
        | _ -> analysis.escapeOfTrailing

// Whether the rest of the program forwards `name` as its result: never when the trailing
// expression's chain ends in anything but a bare variable, else decided by the walk.
let topLevelTailForwarded (name: Str) (analysis: TopLevelLoweringAnalysis) =
    match analysis.trailingFinalVar with
        | None -> false
        | Some(_finalVar) -> isTailForwardedBindingResult(analysis.remainingBody)(name)

let recursive topLevelItemsMayReferenceName (items: List(TopLevelItem)) (trailingBody: Expr) (name: Str) =
    match items with
        | [] -> exprMayReferenceName(trailingBody)(name)
        | TopLevelAt(_span, inner) :: rest -> topLevelItemsMayReferenceName(inner :: rest)(trailingBody)(name)
        | TopLevelLet(LetBindingSyntax { value = value }, _isRecursive) :: rest ->
            if exprMayReferenceName(value)(name)
            then true
            else topLevelItemsMayReferenceName(rest)(trailingBody)(name)
        | TopLevelRecursiveGroup(bindings) :: rest ->
            if letBindingSyntaxListMayReferenceName(bindings)(name)
            then true
            else topLevelItemsMayReferenceName(rest)(trailingBody)(name)
        | _other :: rest -> topLevelItemsMayReferenceName(rest)(trailingBody)(name)

// Recognizes ONLY `Ctor(arg)` — one, fully-saturating argument — against a known constructor whose
// scheme is exactly `a -> T(...)` (not itself a function, ruling out a curried/multi-argument
// constructor this slice does not attempt) and that isn't zero-cost (a zero-cost constructor never
// reaches `finishConstructorAllocation`'s `AllocAdt` path at all, so there is nothing to drop).
// Every constructor `standardConstructorLayouts` registers today (`Some`, `Ok`, `Error`) is exactly
// this shape. A partial application of a real multi-argument constructor would also syntactically
// match `ExprCall(ExprVar(ctorName), _, _, _)` here, which is why the scheme's OWN arity is checked
// against — not just that the call produces one argument — before ever answering `Some`.
let directSingleArgRcConstructorLayout (expr: Expr) (state: CoreLoweringState) =
    match stripExprAt(expr) with
        | ExprCall(callee, _arg, _isSugar, _argLayout) ->
            match stripExprAt(callee) with
                | ExprVar(constructorName) ->
                    match constructorLayout(constructorName)(state) with
                        | Some(CoreConstructorLayout { isZeroCost = true }) -> None
                        | Some(CoreConstructorLayout { scheme = TypeScheme { body = SemFunction(_parameterType, resultType, _effect) } } as layout) ->
                            match resultType with
                                | SemFunction(_, _, _) -> None
                                | _ -> Some(layout)
                        | _ -> None
                | _ -> None
        | _ -> None

// Called only once the caller has confirmed `value` is a direct, fully-saturating call to a
// field-carrying constructor and `name` is provably dead. Skips `finishLetValue`'s local-slot/
// binding machinery entirely (nothing will ever read `name` back): lowers the value, immediately
// releases it with a single `RcDrop` (`ownerSlot = -1`, since this value is never stored to a
// local) naming the type's structural dropper when the release reaches past the cell, then
// continues lowering the rest of the program.
let lowerDeadRcTopLevelLet name value layout environment state =
    match rewriteTraitConstrainedTopLevelValue(name)(value)(environment) with
        | TraitConstrainedTopLevelValueRewriting { value = _rewrittenValue, error = Some(error) } -> failure(state)(UnresolvedTraitEvidenceForwarding(error))
        | TraitConstrainedTopLevelValueRewriting { value = rewrittenValue, error = None } ->
            match state
            |> withStateRuntimeAdtRequested(true)
            |> lowerCore(rewrittenValue) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = valueState, temp = valueTemp, semanticType = valueType, error = None } ->
                    match (layout, valueState
                    |> withStateRuntimeAdtRequested(false)
                    |> synthesizeStructuralDropperLabel(valueType)) with
                        | (CoreConstructorLayout { name = constructorName }, (dropperLabel, dropperState)) ->
                            dropperState
                            |> emit(RcDrop(valueTemp)(runtimeManagedAdtTypeName(constructorName))(-1)(true)(false)(dropperLabel))
                            |> success(-1)(SemNever)

// The declaring type's own bare name in a field is shorthand for the type applied to its own
// parameters (`Node(Int, MapTree, K, V, MapTree)` inside `MapTree(K, V)`), the language's one
// exception to the arity rule; every other field converts as written.
let declaringTypeName (resultType: SemanticType) =
    match namedTypeNameOf(resultType) with
        | Some(name) -> name
        | None -> ""

let recursive isDeclaringTypeReference (typeExpr: TypeExpr) (selfName: Str) =
    match typeExpr with
        | TypeAt(_span, inner) -> isDeclaringTypeReference(inner)(selfName)
        | TypeNamed(name) -> name == selfName
        | _ -> false

let recursive constructorFieldSemanticTypes (aliases: MapTree(Str, (List(Str), TypeExpr))) (parameters: List(TypeExpr)) (parameterTypes: List((Str, SemanticType))) (selfName: Str) (selfType: SemanticType) =
    match parameters with
        | [] -> Some([])
        | parameter :: rest ->
            match if isDeclaringTypeReference(parameter)(selfName)
            then Some(selfType)
            else
                typeExprToSemanticType(expandTypeAliases(aliases)(32)(parameter))(parameterTypes) with
                | None -> None
                | Some(fieldType) ->
                    match constructorFieldSemanticTypes(aliases)(rest)(parameterTypes)(selfName)(selfType) with
                        | None -> None
                        | Some(restTypes) -> Some(fieldType :: restTypes)

// A constructor's scheme is a right-associated curried chain ending at the type's own result type
// — `field0 -> field1 -> ... -> T` — matching `standardConstructorLayouts`' intrinsic entries
// exactly (`Some`'s single-field scheme is the one-field case of this same shape).
let recursive buildConstructorSchemeBody (fieldTypes: List(SemanticType)) (resultType: SemanticType) =
    match fieldTypes with
        | [] -> resultType
        | fieldType :: rest ->
            SemFunction(fieldType)(buildConstructorSchemeBody(rest)(resultType))(None)

// Looks up a previously-registered type's own declared arity from its constructors' shared result
// type (`SemNamed(_, name, arguments)` at the end of a constructor's curried scheme — every
// constructor of the same type shares that same result, so the first match found is authoritative).
// Returns `None` both for a genuinely unknown name and for a type not yet registered when its own
// fields are being classified (self/forward reference) — either way, `typeExprArityErrors` below
// treats "not found here" as "nothing to check," preserving the existing lenient fallback a
// self-referential ADT (`type Tree = | Node(Tree)`) or a not-yet-processed forward reference needs.
let recursive resultNamedTypeArity (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(_argument, result, _row) -> resultNamedTypeArity(result)
        | SemNamed(_symbolId, name, arguments) -> Some((name, length(arguments)))
        | _other -> None

let recursive findDeclaredTypeArity (name: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> None
        | CoreConstructorLayout { scheme = TypeScheme { body = body } } :: rest ->
            match resultNamedTypeArity(body) with
                | Some((candidateName, arity)) ->
                    if candidateName == name
                    then Some(arity)
                    else findDeclaredTypeArity(name)(rest)
                | None -> findDeclaredTypeArity(name)(rest)

// A bare reference to a generic type (`Inner` where `Inner` needs one type argument, as opposed to
// `Inner(a)`) previously classified as `SemNamed(0, "Inner", [])` in `typeExprToSemanticType` below
// with no arity check at all — silently wrong rather than diagnosed. This walk runs first and
// reports the first mismatch it finds, so a genuinely wrong field type still gets a specific
// "expects N type argument(s)" message instead of the generic unsupported-field-type fallback.
let recursive typeExprArityErrors (typeExpr: TypeExpr) (layouts: List(CoreConstructorLayout)) =
    match typeExpr with
        | TypeAt(_span, inner) -> typeExprArityErrors(inner)(layouts)
        | TypeNamed(name) ->
            match findDeclaredTypeArity(name)(layouts) with
                | Some(arity) ->
                    if arity == 0
                    then None
                    else Some((name, arity, 0))
                | None -> None
        | TypeApplied("List", element :: []) -> typeExprArityErrors(element)(layouts)
        | TypeApplied(name, arguments) ->
            match findDeclaredTypeArity(name)(layouts) with
                | Some(arity) ->
                    let actualArity = length(arguments)
                    in
                        if arity == actualArity
                        then typeExprArityErrorsList(arguments)(layouts)
                        else Some((name, arity, actualArity))
                | None -> typeExprArityErrorsList(arguments)(layouts)
        | TypeTuple(elements) -> typeExprArityErrorsList(elements)(layouts)
        | TypeArrow(argument, result, _capabilities, _tail) -> typeExprArityErrorsList([argument, result])(layouts)
        | _other -> None
and typeExprArityErrorsList (typeExprs: List(TypeExpr)) (layouts: List(CoreConstructorLayout)) =
    match typeExprs with
        | [] -> None
        | head :: tail ->
            match typeExprArityErrors(head)(layouts) with
                | Some(mismatch) -> Some(mismatch)
                | None -> typeExprArityErrorsList(tail)(layouts)

let arityMismatchMessage name expected actual = "Type '" + name + "' expects " + Ashes.Text.fromInt(expected) + " type argument(s) but got " + Ashes.Text.fromInt(actual) + "."

let buildUserConstructorLayout (aliases: MapTree(Str, (List(Str), TypeExpr))) (resultType: SemanticType) (quantified: List((Int, Str))) (parameterTypes: List((Str, SemanticType))) (tag: Int) (layouts: List(CoreConstructorLayout)) (constructor: TypeConstructor) =
    match constructor with
        | TypeConstructor { name = name, parameters = parameters, fieldNames = fieldNames } ->
            match typeExprArityErrorsList(parameters)(layouts) with
                | Some((typeName, expected, actual)) ->
                    actual
                    |> arityMismatchMessage(typeName)(expected)
                    |> UnsupportedTypeDeclaration
                    |> Error
                | None ->
                    match constructorFieldSemanticTypes(aliases)(parameters)(parameterTypes)(declaringTypeName(resultType))(resultType) with
                        | None -> Error(UnsupportedTypeDeclaration("constructor '" + name + "' has a field type outside the supported scalar/type-parameter set (Int, Str, Bool, Float, BigInt, Rune, Bytes, Unit, one of the type's own type parameters, or a pure function over those)"))
                        | Some(fieldTypes) ->
                            Ok(CoreConstructorLayout(
                                name = name,
                                tag = tag,
                                scheme = TypeScheme(quantified = quantified, body = buildConstructorSchemeBody(fieldTypes)(resultType), constraints = []),
                                fieldNames = fieldNames,
                                isZeroCost = false,
                                tagless = false
                            ))

let recursive buildUserConstructorLayoutsFromIndex (aliases: MapTree(Str, (List(Str), TypeExpr))) (resultType: SemanticType) (quantified: List((Int, Str))) (parameterTypes: List((Str, SemanticType))) (index: Int) (layouts: List(CoreConstructorLayout)) (constructors: List(TypeConstructor)) =
    match constructors with
        | [] -> Ok([])
        | constructor :: rest ->
            match buildUserConstructorLayout(aliases)(resultType)(quantified)(parameterTypes)(index)(layouts)(constructor) with
                | Error(error) -> Error(error)
                | Ok(layout) ->
                    match buildUserConstructorLayoutsFromIndex(aliases)(resultType)(quantified)(parameterTypes)(index + 1)(layouts)(rest) with
                        | Error(error) -> Error(error)
                        | Ok(restLayouts) -> Ok(layout :: restLayouts)

// Assigns each of a type's own declared type parameters a fresh id drawn from the LIVE, per-
// lowering `typeSupply` — unlike `standardConstructorLayouts`' intrinsic schemes (statically
// embedded before the supply starts, needing permanently reserved ids to avoid a self-referential
// substitution, see `reservedBuiltinTypeVariableCount`'s own comment), a user type's layout is
// built fresh during lowering with direct access to the state's own supply, so no reservation is
// needed at all: each id is minted once, here, and never reused.
let freshTypeVariableId (semanticType: SemanticType) =
    match semanticType with
        | SemVariable(id) -> id
        | _other -> Ashes.IO.panic("freshTypeVariable did not return a SemVariable")

let recursive assignTypeParameterIds (parameters: List(TypeParameter)) (supply: TypeVariableSupply) =
    match parameters with
        | [] -> ([], supply)
        | TypeParameter { name = name } :: rest ->
            match freshTypeVariable(supply) with
                | (freshVariable, nextSupply) ->
                    match assignTypeParameterIds(rest)(nextSupply) with
                        | (restPairs, finalSupply) -> ((name, freshTypeVariableId(freshVariable)) :: restPairs, finalSupply)

let recursive typeParameterSemVars (namedIds: List((Str, Int))) =
    match namedIds with
        | [] -> []
        | (_name, id) :: rest -> SemVariable(id) :: typeParameterSemVars(rest)

let recursive typeParameterResolutionTable (namedIds: List((Str, Int))) =
    match namedIds with
        | [] -> []
        | (name, id) :: rest -> (name, SemVariable(id)) :: typeParameterResolutionTable(rest)

let recursive typeParameterQuantified (namedIds: List((Str, Int))) =
    match namedIds with
        | [] -> []
        | (name, id) :: rest -> (id, name) :: typeParameterQuantified(rest)

// Registers one `CoreConstructorLayout` per constructor of a top-level `type` declaration into
// `constructorLayouts` — the same list `standardConstructorLayouts` seeds intrinsically, read
// live at lookup time by `findConstructorLayout`/`constructorLayout`, so a later `TopLevelLet`
// referencing this type's constructors resolves correctly without any further wiring:
// `lowerRecord`/`lowerConstructor`/`emitRecordFieldLoad` already handle any registered layout the
// same way regardless of where it came from — including a genuinely polymorphic one, the exact same
// mechanism `print`'s own `forall a. a -> Unit` scheme and `Some`'s `forall a. a -> Maybe(a)` scheme
// already prove works at every call site. A type parameter's own id is quantified in the scheme,
// so `instantiate` mints a fresh variable per use, never confusing two different call sites'
// instantiations with each other.
// language.md's "4. Algebraic Data Types" section: "canonical Ashes source should declare [type
// parameters] explicitly," but for migration compatibility a payload name with no explicit
// parameter list is an implicit type parameter "only when it denotes no known type" — a
// self-recursive field, a primitive, a compiler-provided runtime type, or any other
// already-registered type stays concrete (`type AppError = | Json(JsonError)` refers to the real
// `JsonError` type, not a fresh parameter; `type Holder = | file: FileHandle` holds the real
// handle type); only a genuinely unknown name, like `a` in `type Inner = | Inner(a)`, becomes one.
let isPrimitiveTypeName name =
    match name with
        | "Int" -> true
        | "Str" -> true
        | "Bool" -> true
        | "Float" -> true
        | "BigInt" -> true
        | "Rune" -> true
        | "Bytes" -> true
        | _other -> false

let recursive containsTypeName names target =
    match names with
        | [] -> false
        | head :: tail ->
            if head == target
            then true
            else containsTypeName(tail)(target)

// The ADTs and handle types the compiler seeds itself (`BuiltinRegistry.CreateBuiltinTypes`), which
// no user `type` declaration registers a layout for.
let isBuiltinRuntimeTypeName name = name == "Unit" || name == "List" || name == "Maybe" || name == "Result" || name == "Socket" || name == "TlsSocket" || name == "Task" || name == "JoinHandle" || name == "Process" || name == "FileHandle"

// Stage 0's `knownTypeNames`: the type itself, the primitives and builtin runtime types, the
// external opaque types, the types registered so far, and every type the program declares
// anywhere (`declaredTypeNames`, collected before any declaration is registered), so a
// constructor field naming a type declared later is a forward reference, not an implicit type
// parameter.
let isKnownTypeName name selfName layouts externalOpaqueTypes (declaredTypeNames: MapTree(Str, Bool)) =
    if name == selfName
    then true
    else
        if isPrimitiveTypeName(name) || isBuiltinRuntimeTypeName(name)
        then true
        else
            if containsTypeName(externalOpaqueTypes)(name)
            then true
            else
                match Ashes.Collection.Map.getStr(name)(declaredTypeNames) with
                    | Some(_declared) -> true
                    | None ->
                        match findDeclaredTypeArity(name)(layouts) with
                            | Some(_arity) -> true
                            | None -> false

let recursive collectImplicitTypeParameterNames (typeExpr: TypeExpr) selfName layouts externalOpaqueTypes (declaredTypeNames: MapTree(Str, Bool)) acc =
    match typeExpr with
        | TypeAt(_span, inner) -> collectImplicitTypeParameterNames(inner)(selfName)(layouts)(externalOpaqueTypes)(declaredTypeNames)(acc)
        | TypeNamed(name) ->
            if isKnownTypeName(name)(selfName)(layouts)(externalOpaqueTypes)(declaredTypeNames)
            then acc
            else
                if containsTypeName(acc)(name)
                then acc
                else append(acc)([name])
        | TypeApplied("List", element :: []) -> collectImplicitTypeParameterNames(element)(selfName)(layouts)(externalOpaqueTypes)(declaredTypeNames)(acc)
        | TypeApplied(_name, arguments) -> collectImplicitTypeParameterNamesList(arguments)(selfName)(layouts)(externalOpaqueTypes)(declaredTypeNames)(acc)
        | TypeTuple(elements) -> collectImplicitTypeParameterNamesList(elements)(selfName)(layouts)(externalOpaqueTypes)(declaredTypeNames)(acc)
        | TypeArrow(argument, result, _capabilities, _tail) -> collectImplicitTypeParameterNamesList([argument, result])(selfName)(layouts)(externalOpaqueTypes)(declaredTypeNames)(acc)
        | _other -> acc
and collectImplicitTypeParameterNamesList (typeExprs: List(TypeExpr)) selfName layouts externalOpaqueTypes (declaredTypeNames: MapTree(Str, Bool)) acc =
    match typeExprs with
        | [] -> acc
        | head :: tail ->
            acc
            |> collectImplicitTypeParameterNames(head)(selfName)(layouts)(externalOpaqueTypes)(declaredTypeNames)
            |> collectImplicitTypeParameterNamesList(tail)(selfName)(layouts)(externalOpaqueTypes)(declaredTypeNames)

let recursive collectImplicitTypeParametersFromConstructors (constructors: List(TypeConstructor)) selfName layouts externalOpaqueTypes (declaredTypeNames: MapTree(Str, Bool)) acc =
    match constructors with
        | [] -> acc
        | TypeConstructor { parameters = parameters } :: rest ->
            acc
            |> collectImplicitTypeParameterNamesList(parameters)(selfName)(layouts)(externalOpaqueTypes)(declaredTypeNames)
            |> collectImplicitTypeParametersFromConstructors(rest)(selfName)(layouts)(externalOpaqueTypes)(declaredTypeNames)

let recursive namesToTypeParameters (names: List(Str)) =
    match names with
        | [] -> []
        | head :: tail -> TypeParameter(name = head) :: namesToTypeParameters(tail)

// Whether `name` names the `Ashes` root module or one of the compiler's own built-in runtime types
// (the ADTs `BuiltinRegistry.CreateBuiltinTypes` seeds — `Unit`/`List`/`Maybe`/`Result`/`Socket`/
// `TlsSocket`/`Task`/`JoinHandle`/`Process`/`FileHandle` — plus the primitive type names
// `BuiltinRegistry.PrimitiveTypeNames` reserves — `Float`/`Bytes`/`Rune`/`u8`/`u16`/`u32`/`u64`), so
// user code may not redeclare it with a top-level `type`.
let isReservedTypeName name = name == "Ashes" || isBuiltinRuntimeTypeName(name) || name == "Float" || name == "Bytes" || name == "Rune" || name == "u8" || name == "u16" || name == "u32" || name == "u64"

let recursive layoutSchemes (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | CoreConstructorLayout { scheme = scheme } :: rest -> scheme :: layoutSchemes(rest)

let recursive markTaglessLayouts (state: CoreLoweringState) (index: TaglessConstructorIndex) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | (CoreConstructorLayout { scheme = scheme, isZeroCost = isZeroCost } as layout) :: rest ->
            (layout with tagless = isTaglessAdtConstructorPrepared(given (name: Str) -> isDeclaredResourceName(name)(state))(index)(isZeroCost)(scheme)) :: markTaglessLayouts(state)(index)(rest)

// The layouts of one type declaration with their tagless flag decided (see TaglessAdtLayout). The
// sibling count and the resource walk see every registered constructor scheme, the declaration's
// own included, so a self-referential field and an earlier resource-bearing type are both visible.
let decideTaglessLayouts (state: CoreLoweringState) (newLayouts: List(CoreConstructorLayout)) =
    (let group = state.programState
    in
        let registered = group.constructorIndex
        in
            markTaglessLayouts(state)(extendTaglessConstructorIndex(layoutSchemes(newLayouts))(registered.taglessShapes))(newLayouts))

let recursive markZeroCostLayouts (isZeroCost: Bool) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | layout :: rest -> (layout with isZeroCost = isZeroCost) :: markZeroCostLayouts(isZeroCost)(rest)

// Stage 0's `RegisterTypeDeclaration`: a declared type's constructor layouts. A zero-cost nominal
// type registers its one constructor marked zero-cost, which construction and matching represent
// as the payload itself.
let registerTypeDeclarationWith (isZeroCost: Bool) (declaration: TypeDecl) (state: CoreLoweringState) =
    match declaration with
        | TypeDecl { name = name, typeParameters = typeParameters, constructors = constructors } ->
            if isReservedTypeName(name)
            then Error(ReservedTypeName("'Ashes' and built-in runtime types are reserved"))
            else
                match state with
                    | CoreLoweringState { typeSupply = supply, externalOpaqueTypes = externalOpaqueTypes, programState = CoreProgramState { constructorLayouts = existingLayouts, declaredTypeNames = declaredTypeNames } } ->
                        let effectiveTypeParameters =
                            match typeParameters with
                                | [] ->
                                    []
                                    |> collectImplicitTypeParametersFromConstructors(constructors)(name)(existingLayouts)(externalOpaqueTypes)(declaredTypeNames)
                                    |> namesToTypeParameters
                                | explicit -> explicit
                        in
                            match assignTypeParameterIds(effectiveTypeParameters)(supply) with
                                | (namedIds, nextSupply) ->
                                    let resultType =
                                        namedIds
                                        |> typeParameterSemVars
                                        |> SemNamed(0)(name)
                                    in
                                        let quantified = typeParameterQuantified(namedIds)
                                        in
                                            let parameterTypes = typeParameterResolutionTable(namedIds)
                                            in
                                                match buildUserConstructorLayoutsFromIndex(stateTypeAliases(state))(resultType)(quantified)(parameterTypes)(0)(existingLayouts)(constructors) with
                                                    | Error(error) -> Error(error)
                                                    | Ok(newLayouts) ->
                                                        let decidedLayouts =
                                                            newLayouts
                                                            |> markZeroCostLayouts(isZeroCost)
                                                            |> decideTaglessLayouts(state)
                                                        in
                                                            (state with typeSupply = nextSupply)
                                                            |> withAppendedConstructorLayouts(decidedLayouts)
                                                            |> withStateCoverageTypes(extendCoverageEnvironment(stateCoverageTypes(state))(decidedLayouts))
                                                            |> withDroppedHeapLayoutFacts
                                                            |> Ok

// The runtime capabilities the compiler provides itself; a user `capability` may not redeclare one.
let isReservedCapabilityName name =
    match name with
        | "ConsoleIO" -> true
        | "FileRead" -> true
        | "FileWrite" -> true
        | "ProcessSpawn" -> true
        | "ProcessExit" -> true
        | "TimeRead" -> true
        | "EnvironmentRead" -> true
        | "Entropy" -> true
        | "UnsafeFfi" -> true
        | "NetListen" -> true
        | "NetConnect" -> true
        | "Stop" -> true
        | _ -> false

let recursive capabilityOperationLayouts (capabilityName: Str) (operations: List(CapabilityOperation)) (index: Int) (reversed: List(CoreCapabilityOperationLayout)) =
    match operations with
        | [] ->
            reversed
            |> reverse
            |> Ok
        | CapabilityOperation { name = name } :: rest ->
            match findCapabilityOperationIndex(name)(reversed) with
                | Some(_existing) ->
                    name
                    |> DuplicateCapabilityOperationName(capabilityName)
                    |> Error
                | None -> capabilityOperationLayouts(capabilityName)(rest)(index + 1)(CoreCapabilityOperationLayout(name = name, index = index) :: reversed)

let recursive parameterTableTypes (parameterTypes: List((Str, SemanticType))) =
    match parameterTypes with
        | [] -> []
        | (_name, parameterType) :: rest -> parameterType :: parameterTableTypes(rest)

// An operation's declared signature as a scheme quantified over the capability's own type
// parameters; an unsigned operation, or one whose signature this resolver cannot express, gets no
// scheme and is typed by a fresh variable at each perform site. The scheme's body pairs the
// capability instance with the signature, `(Cap(params), signature)`, so one instantiation at a
// perform site yields both the operation's type and the instance's type arguments.
let recursive capabilityOperationSchemes (aliases: MapTree(Str, (List(Str), TypeExpr))) (capabilityName: Str) (operations: List(CapabilityOperation)) (quantified: List((Int, Str))) (parameterTypes: List((Str, SemanticType))) =
    match operations with
        | [] -> []
        | CapabilityOperation { name = name, signature = signature } :: rest ->
            match signature with
                | None -> capabilityOperationSchemes(aliases)(capabilityName)(rest)(quantified)(parameterTypes)
                | Some(typeExpr) ->
                    match typeExprToSemanticType(expandTypeAliases(aliases)(32)(typeExpr))(parameterTypes) with
                        | None -> capabilityOperationSchemes(aliases)(capabilityName)(rest)(quantified)(parameterTypes)
                        | Some(operationType) ->
                            let instance =
                                parameterTypes
                                |> parameterTableTypes
                                |> SemCapability(capabilityName)
                            in (capabilityName, name, TypeScheme(quantified = quantified, body = SemTuple([instance, operationType]), constraints = [])) :: capabilityOperationSchemes(aliases)(capabilityName)(rest)(quantified)(parameterTypes)

// Registers one `CoreCapabilityLayout` per top-level `capability` declaration, numbered in
// declaration order: the number is the capability's handler-evidence global, and the operations
// are numbered in declaration order within the handler frame. The operations' signatures are
// recorded for the perform sites.
let registerTopLevelCapabilityDeclaration (declaration: CapabilityDecl) (state: CoreLoweringState) =
    match declaration with
        | CapabilityDecl { name = name, typeParameters = typeParameters, operations = operations } ->
            if isReservedCapabilityName(name)
            then Error(ReservedCapabilityName(name))
            else
                match state
                |> stateCapabilityLayouts
                |> findCapabilityLayout(name) with
                    | Some(_existing) -> Error(DuplicateCapabilityName(name))
                    | None ->
                        match capabilityOperationLayouts(name)(operations)(0)([]) with
                            | Error(error) -> Error(error)
                            | Ok(operationLayouts) ->
                                match assignTypeParameterIds(typeParameters)(state.typeSupply) with
                                    | (namedIds, nextSupply) ->
                                        let schemes =
                                            namedIds
                                            |> typeParameterResolutionTable
                                            |> capabilityOperationSchemes(stateTypeAliases(state))(name)(operations)(typeParameterQuantified(namedIds))
                                        in
                                            (state with typeSupply = nextSupply)
                                            |> withStateCapabilityLayouts(append(stateCapabilityLayouts(state))([CoreCapabilityLayout(name = name, index = stateCapabilityGlobalCount(state), operations = operationLayouts)]))
                                            |> withStateCapabilityGlobalCount(stateCapabilityGlobalCount(state) + 1)
                                            |> withStateCapabilityOperationSchemes(append(stateCapabilityOperationSchemes(state))(schemes))
                                            |> Ok

let recursive providerBindingOperations (bindings: List(ProvideBinding)) =
    match bindings with
        | [] -> []
        | ProvideBinding { operationName = operationName, implementation = implementation } :: rest -> (operationName, implementation) :: providerBindingOperations(rest)

let recursive providerTypeArguments (aliases: MapTree(Str, (List(Str), TypeExpr))) (typeArguments: List(TypeExpr)) =
    match typeArguments with
        | [] -> Some([])
        | typeArgument :: rest ->
            match typeExprToSemanticType(expandTypeAliases(aliases)(32)(typeArgument))([]) with
                | None -> None
                | Some(argument) ->
                    match providerTypeArguments(aliases)(rest) with
                        | None -> None
                        | Some(arguments) -> Some(argument :: arguments)

// Registers a `provide` under its capability and concrete instance, as stage 0's
// `RegisterProviderDeclarations` does. Whether the provider names a declared capability, supplies
// every operation once and is the only provider of its instance is the type checker's to report;
// a provider lowering cannot use (an unknown capability or type argument, a second provider of an
// instance) is not registered.
let registerProviderDeclaration (declaration: ProvideDecl) (state: CoreLoweringState) =
    match declaration with
        | ProvideDecl { capabilityName = name, typeArguments = typeArguments, bindings = bindings } ->
            match (state
            |> stateCapabilityLayouts
            |> findCapabilityLayout(name), providerTypeArguments(stateTypeAliases(state))(typeArguments)) with
                | (Some(_layout), Some(arguments)) ->
                    match state
                    |> stateStaticProviders
                    |> findStaticProviderExact(name)(arguments) with
                        | Some(_existing) -> state
                        | None ->
                            withStateStaticProviders(append(stateStaticProviders(state))([CoreStaticProviderLayout(capabilityName = name, typeArguments = arguments, operations = providerBindingOperations(bindings))]))(state)
                | _ -> state

let recursive registerProgramProviders (items: List(TopLevelItem)) (state: CoreLoweringState) =
    match items with
        | [] -> state
        | TopLevelAt(_span, inner) :: rest -> registerProgramProviders(inner :: rest)(state)
        | TopLevelProvide(declaration) :: rest ->
            state
            |> registerProviderDeclaration(declaration)
            |> registerProgramProviders(rest)
        | _other :: rest -> registerProgramProviders(rest)(state)

// Every capability declaration is registered before any value is lowered: a perform site sizes
// its handler frame and addresses the post registers by the program's total capability count.
// The static providers are registered by `registerProgramProviders` once every capability and type
// is known.
let recursive registerProgramCapabilities (items: List(TopLevelItem)) (state: CoreLoweringState) =
    match items with
        | [] -> Ok(state)
        | TopLevelAt(_span, inner) :: rest -> registerProgramCapabilities(inner :: rest)(state)
        | TopLevelCapability(declaration) :: rest ->
            match registerTopLevelCapabilityDeclaration(declaration)(state) with
                | Error(error) -> Error(error)
                | Ok(nextState) -> registerProgramCapabilities(rest)(nextState)
        | _other :: rest -> registerProgramCapabilities(rest)(state)

// Stage 0's `CapabilityHandlerGlobals`: one evidence global per capability plus the post register
// and the live-post counter, or none at all for a program without capabilities.
let capabilityHandlerGlobalCount (state: CoreLoweringState) =
    if stateCapabilityGlobalCount(state) == 0
    then 0
    else stateCapabilityGlobalCount(state) + 2

// The compiler name of the binding a top-level item declares, if any; the trailing expression,
// which follows the last item, goes by the empty name.
let recursive programItemBindingName (items: List(TopLevelItem)) =
    match items with
        | TopLevelAt(_span, inner) :: _rest -> programItemBindingName([inner])
        | TopLevelLet(LetBindingSyntax { name = name }, _isRecursive) :: _rest -> Some(name)
        | TopLevelRecursiveGroup(LetBindingSyntax { name = name } :: _bindings) :: _rest -> Some(name)
        | [] -> Some("")
        | _ -> None

// The source region the next item's spans resolve in: the region its binding was declared in.
let withItemSourceRegion (items: List(TopLevelItem)) (state: CoreLoweringState) =
    match programItemBindingName(items) with
        | Some(name) -> withStateSourceRegionOf(name)(state)
        | None -> state

// Lowers a whole program's top-level items one at a time, threading lowering state through them,
// rather than desugaring into one big nested-let expression up front: a top-level
// `let recursive ... and ...` group has no expression-level representation (the language only
// allows `and` groups as top-level declarations, never nested inside another expression), so its
// members must go through lowerPreparedRecursiveGroupWith's own member/continuation split, with
// "the rest of the program" supplied as the continuation lower rather than as a literal Expr — the
// continuation lower ignores the placeholder body it's handed and lowers the remaining items
// instead. Capability declarations are registered ahead of the value chain by
// `registerProgramCapabilities`; type declarations register their layouts in place; external,
// provider, trait, and implementation declarations are
// registered ahead of lowering by inference and are not part of the value chain, so they are
// skipped here rather than lowered. `environment` is `Some` only from lowerCoreProgramWithEnvironment
// — it enables the earlier environment-driven trait rewrite. Core also infers and lowers
// supported constrained ordinary bindings directly, including recursive evidence forwarding.
// Walks the flat top-level sequence iteratively: each declaration is opened (its value lowered and
// stored, its names in scope) and the close it still owes pushed on `pending`, the trailing body is
// lowered last, and closePendingTopLevelItems then closes the chain innermost first.
let recursive lowerCoreProgramItems items trailingBody seen environment (analysis: TopLevelLoweringAnalysis) (pending: List(PendingTopLevelClose)) itemState =
    (let state = withItemSourceRegion(items)(itemState)
    in
        match items with
            | [] ->
                state
                |> observeTopLevelScope
                |> lowerCore(trailingBody)
                |> closePendingTopLevelItems(pending)
            // currentSpan carries the declaration as the innermost enclosing span, so an instruction the
            // binding emits outside its value expression — the StoreLocal of the bound value — is tagged
            // with the declaration rather than left unpositioned. A flat top-level declaration otherwise has
            // no position at all, and a debugger stepping over it reports whatever line came before.
            | TopLevelAt(span, inner) :: rest ->
                (state with currentSpan = Some(span))
                |> withStateRecursiveDeclarationSpan(Some(span))
                |> lowerCoreProgramItems(inner :: rest)(trailingBody)(seen)(environment)(analysis)(pending)
            | TopLevelType(_declaration) :: rest -> lowerCoreProgramItems(rest)(trailingBody)(seen)(environment)(analysis)(pending)(state)
            | TopLevelLet(LetBindingSyntax { name = name, value = value, typeAnnotation = annotation, requirements = requirements }, false) :: rest ->
                match checkTopLevelNames([name])(seen) with
                    | TopLevelDuplicateCheck { duplicate = Some(duplicateName) } -> failure(state)(DuplicateTopLevelBinding(duplicateName))
                    | TopLevelDuplicateCheck { seen = nextSeen, duplicate = None } ->
                        match state with
                            | CoreLoweringState { bindings = outerBindings } ->
                                let nextAnalysis = peelAnalysis(1)(analysis)
                                in
                                    match directSingleArgRcConstructorLayout(value)(state) with
                                        | Some(layout) ->
                                            if topLevelItemsMayReferenceName(rest)(trailingBody)(name)
                                            then
                                                match openArenaBracketedTopLevelLet(name)(value)(annotation)(requirements)(nextAnalysis.remainingBody)(topLevelEscapeBody(nextAnalysis))(topLevelTailForwarded(name)(nextAnalysis))(environment)(outerBindings)(false)(pending)(state) with
                                                    | OpenedTopLevelChain { chainState = failedState, chainError = Some(error) } -> failure(failedState)(error)
                                                    | OpenedTopLevelChain { chainState = opened, chainPending = nextPending, chainError = None } -> lowerCoreProgramItems(rest)(trailingBody)(nextSeen)(environment)(nextAnalysis)(nextPending)(opened)
                                            else
                                                match lowerDeadRcTopLevelLet(name)(value)(layout)(environment)(state) with
                                                    | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                                                    | LoweredCoreValue { state = dropped, error = None } -> lowerCoreProgramItems(rest)(trailingBody)(nextSeen)(environment)(nextAnalysis)(pending)(dropped)
                                        | None ->
                                            match openArenaBracketedTopLevelLet(name)(value)(annotation)(requirements)(nextAnalysis.remainingBody)(topLevelEscapeBody(nextAnalysis))(topLevelTailForwarded(name)(nextAnalysis))(environment)(outerBindings)(topLevelNameOnlyDirectCallee(name)(analysis.nonCalleeUses))(pending)(state) with
                                                | OpenedTopLevelChain { chainState = failedState, chainError = Some(error) } -> failure(failedState)(error)
                                                | OpenedTopLevelChain { chainState = opened, chainPending = nextPending, chainError = None } -> lowerCoreProgramItems(rest)(trailingBody)(nextSeen)(environment)(nextAnalysis)(nextPending)(opened)
            | TopLevelLet(LetBindingSyntax { name = name, value = value, typeAnnotation = annotation, requirements = requirements }, true) :: rest ->
                match checkTopLevelNames([name])(seen) with
                    | TopLevelDuplicateCheck { duplicate = Some(duplicateName) } -> failure(state)(DuplicateTopLevelBinding(duplicateName))
                    | TopLevelDuplicateCheck { seen = nextSeen, duplicate = None } ->
                        match state with
                            | CoreLoweringState { bindings = outerBindings, nextLambdaId = lambdaId } ->
                                match openPossiblyConstrainedRecursive(name)(value)(annotation)(requirements)(lowerCore)(state) with
                                    | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                                    | LoweredCoreValue { state = opened, error = None } ->
                                        lowerCoreProgramItems(rest)(trailingBody)(nextSeen)(environment)(peelAnalysis(1)(analysis))(pendingBindingsRestore(outerBindings) :: pending)(opened)
            | TopLevelRecursiveGroup(bindings) :: rest ->
                match checkTopLevelNames(letBindingSyntaxNames(bindings))(seen) with
                    | TopLevelDuplicateCheck { duplicate = Some(duplicateName) } -> failure(state)(DuplicateTopLevelBinding(duplicateName))
                    | TopLevelDuplicateCheck { seen = nextSeen, duplicate = None } ->
                        match state with
                            | CoreLoweringState { bindings = outerBindings } ->
                                let pairs = letBindingSyntaxPairs(bindings)(state)
                                in
                                    match state
                                    |> groupMemberStarts(bindings)
                                    |> openPossiblyConstrainedGroup(bindings)(pairs)(lowerCore) with
                                        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                                        | LoweredCoreValue { state = opened, error = None } ->
                                            lowerCoreProgramItems(rest)(trailingBody)(nextSeen)(environment)(peelAnalysis(length(bindings))(analysis))(pendingBindingsRestore(outerBindings) :: pending)(opened)
            | _ :: rest -> lowerCoreProgramItems(rest)(trailingBody)(seen)(environment)(analysis)(pending)(state))

// Stage 0 registers every type declaration before it lowers a value (`RegisterTypeDeclarations`
// ahead of the item walk), so a constructor is known to every analysis that runs ahead of the
// declaration's own position, the whole-program provenance fixpoint among them; the item walk
// then passes the declarations by.
// Every type name the program declares (a type, a type alias, a zero-cost type, or an external
// opaque type), known to the registration of each declaration before any is registered, as
// stage 0's `knownTypeNames` is.
let recursive declaredTypeNamesOf (items: List(TopLevelItem)) (names: MapTree(Str, Bool)) =
    match items with
        | [] -> names
        | TopLevelAt(_span, inner) :: rest -> declaredTypeNamesOf(inner :: rest)(names)
        | TopLevelType(TypeDecl { name = name }) :: rest ->
            names
            |> Ashes.Collection.Map.setStr(name)(true)
            |> declaredTypeNamesOf(rest)
        | TopLevelExternal(ExternalOpaqueType(name, _resource)) :: rest ->
            names
            |> Ashes.Collection.Map.setStr(name)(true)
            |> declaredTypeNamesOf(rest)
        | TopLevelTypeAlias(TypeAliasDecl { name = name }) :: rest ->
            names
            |> Ashes.Collection.Map.setStr(name)(true)
            |> declaredTypeNamesOf(rest)
        | TopLevelZeroCostType(ZeroCostTypeDecl { name = name }) :: rest ->
            names
            |> Ashes.Collection.Map.setStr(name)(true)
            |> declaredTypeNamesOf(rest)
        | _ :: rest -> declaredTypeNamesOf(rest)(names)

let recursive registerDeclaredOrdinaryTypes (items: List(TopLevelItem)) (state: CoreLoweringState) =
    match items with
        | [] -> Ok(state)
        | TopLevelAt(_span, inner) :: rest -> registerDeclaredOrdinaryTypes(inner :: rest)(state)
        | TopLevelType(declaration) :: rest ->
            match registerTypeDeclarationWith(false)(declaration)(state) with
                | Error(error) -> Error(error)
                | Ok(registered) -> registerDeclaredOrdinaryTypes(rest)(registered)
        | _ :: rest -> registerDeclaredOrdinaryTypes(rest)(state)

let recursive registerDeclaredZeroCostTypes (items: List(TopLevelItem)) (state: CoreLoweringState) =
    match items with
        | [] -> Ok(state)
        | TopLevelAt(_span, inner) :: rest -> registerDeclaredZeroCostTypes(inner :: rest)(state)
        | TopLevelZeroCostType(ZeroCostTypeDecl { name = name, typeParameters = typeParameters, constructor = constructor, derivingTraits = derivingTraits }) :: rest ->
            match registerTypeDeclarationWith(true)(TypeDecl(name = name, typeParameters = typeParameters, constructors = [constructor], isRecord = false, derivingTraits = derivingTraits))(state) with
                | Error(error) -> Error(error)
                | Ok(registered) -> registerDeclaredZeroCostTypes(rest)(registered)
        | _ :: rest -> registerDeclaredZeroCostTypes(rest)(state)

// Stage 0's `RegisterTypeDeclarations`: every ordinary type declaration, then every zero-cost one.
let registerDeclaredTypes (items: List(TopLevelItem)) (state: CoreLoweringState) =
    match registerDeclaredOrdinaryTypes(items)(state) with
        | Error(error) -> Error(error)
        | Ok(registered) -> registerDeclaredZeroCostTypes(items)(registered)

let recursive queryAliasParameters names index =
    match names with
        | [] -> []
        | name :: rest -> (name, SemParameter(index)(name)) :: queryAliasParameters(rest)(index + 1)

let recursive queryAliasParameterIds parameters =
    match parameters with
        | [] -> []
        | (_name, SemParameter(id, _parameterName)) :: rest -> id :: queryAliasParameterIds(rest)
        | _ :: rest -> queryAliasParameterIds(rest)

let withQueryAliasContext (state: CoreLoweringState) =
    (let aliases = stateTypeAliases(state)
    in
        let coverage = stateCoverageTypes(state)
        in
            let context =
                Ashes.Collection.Map.foldLeft(given (context) ->
                    given (name) ->
                        given (entry) ->
                            match entry with
                                | (names, target) ->
                                    let parameters = queryAliasParameters(names)(0)
                                    in
                                        match typeExprToSemanticType(expandTypeAliases(aliases)(32)(target))(parameters) with
                                            | Some(resolved) ->
                                                addTypeAliasDefinition(name)(queryAliasParameterIds(parameters))(resolved)(context)
                                            | None -> context)(coverage.typeResolutionContext)(aliases)
            in withStateCoverageTypes((coverage with typeResolutionContext = context))(state))

// The declared types' layout facts are classified once every declaration is registered: each
// declaration only drops the table, and a question asked meanwhile classifies its type live.
let registerProgramTypes (items: List(TopLevelItem)) (state: CoreLoweringState) =
    match state
    |> withStateDeclaredTypeNames(declaredTypeNamesOf(items)(Ashes.Collection.Map.empty))
    |> withStateTypeAliases(typeAliasesOf(items)(Ashes.Collection.Map.empty))
    |> registerDeclaredTypes(items) with
        | Error(error) -> Error(error)
        | Ok(registered) ->
            registered
            |> withStateDropperTypes(registered
            |> stateConstructorLayouts
            |> constructorInferenceDefinitionsFromLayouts
            |> prepareDropperTypes)
            |> withQueryAliasContext
            |> withRefreshedHeapLayoutFacts
            |> Ok

// Stage 0's `RegisterExternalFunctions`: every `external` declaration of the program, typed
// against a resolution context that names the program's own types (under symbol id 0, the id
// this lowering gives every type), the compiler-provided `Maybe`, `Result` and `Unit`, and the
// opaque external types, so a call to an external function finds its layout by the declared
// (stitched) name and the arguments and result carry the declaration's source types.
let recursive collectExternalDeclarationsOf (items: List(TopLevelItem)) (reversed: List(ExternalDecl)) =
    match items with
        | [] -> reverse(reversed)
        | TopLevelAt(_span, inner) :: rest -> collectExternalDeclarationsOf(inner :: rest)(reversed)
        | TopLevelExternal(declaration) :: rest -> collectExternalDeclarationsOf(rest)(declaration :: reversed)
        | _ :: rest -> collectExternalDeclarationsOf(rest)(reversed)

// A parameterless type alias an external signature may name, as stage 0's
// `ResolveDeclaredExternalType` resolves it.
let externalAliasDefinition (aliases: MapTree(Str, (List(Str), TypeExpr))) (name: Str) (target: TypeExpr) context =
    match typeExprToSemanticType(expandTypeAliases(aliases)(32)(target))([]) with
        | Some(resolved) -> addTypeAliasDefinition(name)([])(resolved)(context)
        | None -> context

// A zero-cost type an external signature may name: one without type parameters is represented
// across the boundary as its payload, as stage 0's `EraseZeroCostTypeRepresentation` erases it.
let externalZeroCostDefinition (aliases: MapTree(Str, (List(Str), TypeExpr))) (name: Str) (typeParameters: List(TypeParameter)) (constructor: TypeConstructor) context =
    match (typeParameters, constructor) with
        | ([], TypeConstructor { parameters = payload :: [] }) ->
            match typeExprToSemanticType(expandTypeAliases(aliases)(32)(payload))([]) with
                | Some(representation) -> addZeroCostTypeDefinitionWithProvenance(0)(name)([])(representation)(DeclarationProvenance(packageId = "standalone"))(context)
                | None -> addTypeDefinition(0)(name)(0)(context)
        | _ ->
            addTypeDefinition(0)(name)(length(typeParameters))(context)

let recursive externalResolutionContextOf (aliases: MapTree(Str, (List(Str), TypeExpr))) (items: List(TopLevelItem)) context =
    match items with
        | [] -> context
        | TopLevelAt(_span, inner) :: rest -> externalResolutionContextOf(aliases)(inner :: rest)(context)
        | TopLevelType(TypeDecl { name = name, typeParameters = typeParameters }) :: rest ->
            context
            |> addTypeDefinition(0)(name)(length(typeParameters))
            |> externalResolutionContextOf(aliases)(rest)
        | TopLevelZeroCostType(ZeroCostTypeDecl { name = name, typeParameters = typeParameters, constructor = constructor }) :: rest ->
            context
            |> externalZeroCostDefinition(aliases)(name)(typeParameters)(constructor)
            |> externalResolutionContextOf(aliases)(rest)
        | TopLevelTypeAlias(TypeAliasDecl { name = name, typeParameters = [], target = target }) :: rest ->
            context
            |> externalAliasDefinition(aliases)(name)(target)
            |> externalResolutionContextOf(aliases)(rest)
        | TopLevelExternal(ExternalOpaqueType(name, destructor)) :: rest ->
            context
            |> addExternalTypeDefinition(name)(destructor)
            |> externalResolutionContextOf(aliases)(rest)
        | _ :: rest -> externalResolutionContextOf(aliases)(rest)(context)

let standardExternalResolutionContext (unit: Unit) =
    unit
    |> emptyTypeResolutionContext
    |> addTypeDefinition(0)("Maybe")(1)
    |> addTypeDefinition(0)("Result")(2)
    |> addTypeDefinition(0)("Unit")(0)

let externalLayoutOf (abi: ExternalFunctionAbi) =
    match abi with
        | ExternalFunctionAbi { name = name, sourceTyping = ExternalFunctionTyping { directType = directType } } -> CoreExternalFunctionLayout(name = name, abi = abi, scheme = TypeScheme(quantified = [], body = directType, constraints = []))

let recursive externalLayoutsOf (functions: List(ExternalFunctionAbi)) =
    match functions with
        | [] -> []
        | abi :: rest -> externalLayoutOf(abi) :: externalLayoutsOf(rest)

let recursive queryExternalOpaqueContext items context =
    match items with
        | [] -> context
        | TopLevelAt(_span, inner) :: rest -> queryExternalOpaqueContext(inner :: rest)(context)
        | TopLevelExternal(ExternalOpaqueType(name, destructor)) :: rest ->
            context
            |> addExternalTypeDefinition(name)(destructor)
            |> queryExternalOpaqueContext(rest)
        | _ :: rest -> queryExternalOpaqueContext(rest)(context)

let withQueryExternalContext items functions (state: CoreLoweringState) =
    (let coverage = stateCoverageTypes(state)
    in
        withStateCoverageTypes(extendQueryExternals(functions)((coverage with typeResolutionContext = queryExternalOpaqueContext(items)(coverage.typeResolutionContext))))(state))

let registerProgramExternals (items: List(TopLevelItem)) (state: CoreLoweringState) =
    match collectExternalDeclarationsOf(items)([]) with
        | [] -> Ok(state)
        | declarations ->
            match Unit
            |> standardExternalResolutionContext
            |> externalResolutionContextOf(typeAliasesOf(items)(Ashes.Collection.Map.empty))(items)
            |> validateExternalProgramAbi(declarations) with
                | ExternalAbiValidation { metadata = Some(ExternalProgramAbi { functions = functions, opaqueTypes = opaqueTypes }), error = None } ->
                    (state with externalFunctions = append(state.externalFunctions)(functions), externalOpaqueTypes = append(state.externalOpaqueTypes)(opaqueTypes))
                    |> withQueryExternalContext(items)(functions)
                    |> clearAdmissibilityAnswers
                    |> withStateExternalLayouts(functions
                    |> externalLayoutsOf
                    |> append(stateExternalLayouts(state)))
                    |> Ok
                | ExternalAbiValidation { error = Some(error) } ->
                    error
                    |> Ashes.Trait.Show.show
                    |> UnsupportedCoreExternalLowering
                    |> Error
                | _ -> Ok(state)

let isImplementationTypeParameter (name: Str) =
    match name with
        | "u8" -> false
        | "u16" -> false
        | "u32" -> false
        | "u64" -> false
        | _ ->
            if Ashes.Text.length(name) == 0
            then false
            else
                1
                |> Ashes.Text.substring(name)(0)
                |> Ashes.Text.contains("abcdefghijklmnopqrstuvwxyz")

// The type parameters an implementation head mentions, in first-mention order once reversed.
let recursive implementationHeadParameterNames (typeExpr: TypeExpr) (names: List(Str)) =
    match typeExpr with
        | TypeAt(_span, inner) -> implementationHeadParameterNames(inner)(names)
        | TypeNamed(name) ->
            if isImplementationTypeParameter(name) && !containsName(name)(names)
            then name :: names
            else names
        | TypeApplied(_name, arguments) -> implementationHeadParameterNamesOf(arguments)(names)
        | TypeTuple(elements) -> implementationHeadParameterNamesOf(elements)(names)
        | TypeArrow(argument, result, _capabilities, _tail) ->
            names
            |> implementationHeadParameterNames(argument)
            |> implementationHeadParameterNames(result)
        | _other -> names
and implementationHeadParameterNamesOf (typeExprs: List(TypeExpr)) (names: List(Str)) =
    match typeExprs with
        | [] -> names
        | head :: rest ->
            names
            |> implementationHeadParameterNames(head)
            |> implementationHeadParameterNamesOf(rest)

let recursive implementationParameterTypes (names: List(Str)) (index: Int) =
    match names with
        | [] -> []
        | name :: rest -> (name, SemParameter(3000 + index)(name)) :: implementationParameterTypes(rest)(index + 1)

let recursive expandTypeExprAliases (aliases: MapTree(Str, (List(Str), TypeExpr))) (typeExprs: List(TypeExpr)) =
    match typeExprs with
        | [] -> []
        | head :: rest -> expandTypeAliases(aliases)(32)(head) :: expandTypeExprAliases(aliases)(rest)

let implementationTypes (aliases: MapTree(Str, (List(Str), TypeExpr))) (parameters: List((Str, SemanticType))) (typeExprs: List(TypeExpr)) =
    typeExprListToSemanticTypes(expandTypeExprAliases(aliases)(typeExprs))(parameters)

let recursive implementationRequirementConstraints (aliases: MapTree(Str, (List(Str), TypeExpr))) (parameters: List((Str, SemanticType))) (requirements: List(TraitConstraintSyntax)) =
    match requirements with
        | [] -> Some([])
        | TraitConstraintSyntax { traitName = traitName, typeArguments = typeArguments } :: rest ->
            match implementationTypes(aliases)(parameters)(typeArguments) with
                | None -> None
                | Some(argumentTypes) ->
                    match implementationRequirementConstraints(aliases)(parameters)(rest) with
                        | None -> None
                        | Some(restConstraints) -> Some(TraitConstraint(traitName = standardTraitName(traitName), typeArguments = argumentTypes) :: restConstraints)

let recursive implementationMethodDefinitions (bindings: List(TraitImplementationMethodBinding)) =
    match bindings with
        | [] -> []
        | TraitImplementationMethodBinding { methodName = methodName, implementation = implementation } :: rest -> TraitImplementationMethodInferenceDefinition(name = methodName, implementation = implementation, semanticType = SemNever) :: implementationMethodDefinitions(rest)

// A type with its capability rows dropped: lowering tracks no rows, and a written `needs` row
// would make the type unconvertible.
let recursive withoutNeedsRows (typeExpr: TypeExpr) =
    match typeExpr with
        | TypeAt(span, inner) ->
            inner
            |> withoutNeedsRows
            |> TypeAt(span)
        | TypeArrow(argument, result, _capabilities, _tail) ->
            TypeArrow(withoutNeedsRows(argument))(withoutNeedsRows(result))([])(None)
        | TypeApplied(name, arguments) ->
            arguments
            |> withoutNeedsRowsIn
            |> TypeApplied(name)
        | TypeTuple(elements) ->
            elements
            |> withoutNeedsRowsIn
            |> TypeTuple
        | other -> other
and withoutNeedsRowsIn (typeExprs: List(TypeExpr)) =
    match typeExprs with
        | [] -> []
        | head :: rest -> withoutNeedsRows(head) :: withoutNeedsRowsIn(rest)

// A user-declared trait's methods, each with its signature as a scheme over the trait's own
// parameters that requires the trait at them (stage 0's trait method symbols); a signature this
// resolver cannot express leaves the method out.
let recursive userTraitMethodDefinitions (aliases: MapTree(Str, (List(Str), TypeExpr))) (traitName: Str) (parameterTypes: List(SemanticType)) (quantified: List((Int, Str))) (table: List((Str, SemanticType))) (methods: List(TraitMethodDecl)) =
    match methods with
        | [] -> []
        | TraitMethodDecl { name = methodName, signature = signature, defaultImplementation = defaultImplementation } :: rest ->
            match typeExprToSemanticType(signature
            |> withoutNeedsRows
            |> expandTypeAliases(aliases)(32))(table) with
                | Some(methodType) ->
                    let constraint = TraitConstraint(traitName = traitName, typeArguments = parameterTypes)
                    in TraitMethodInferenceDefinition(name = methodName, scheme = TypeScheme(quantified = quantified, body = methodType, constraints = [constraint]), defaultImplementation = defaultImplementation) :: userTraitMethodDefinitions(aliases)(traitName)(parameterTypes)(quantified)(table)(rest)
                | None -> userTraitMethodDefinitions(aliases)(traitName)(parameterTypes)(quantified)(table)(rest)

let recursive userTraitSupertraits (aliases: MapTree(Str, (List(Str), TypeExpr))) (table: List((Str, SemanticType))) (supertraits: List(TraitConstraintSyntax)) =
    match supertraits with
        | [] -> []
        | TraitConstraintSyntax { traitName = traitName, typeArguments = typeArguments } :: rest ->
            match typeExprListToSemanticTypes(expandTypeExprAliases(aliases)(typeArguments))(table) with
                | Some(arguments) -> TraitConstraint(traitName = standardTraitName(traitName), typeArguments = arguments) :: userTraitSupertraits(aliases)(table)(rest)
                | None -> userTraitSupertraits(aliases)(table)(rest)

// Stage 0's trait symbols for a program's own `trait` declarations: each is added to lowering's
// trait environment with its methods and supertraits, so its implementations register against it
// and its methods dispatch as the shipped traits' do. A trait the environment already knows (a
// shipped trait stitched in from `Ashes.Trait`) keeps its seeded definition.
let registerProgramTraitDeclaration (declaration: TraitDecl) (state: CoreLoweringState) =
    match declaration with
        | TraitDecl { name = name, typeParameters = typeParameters, supertraits = supertraits, methods = methods } ->
            let traitName = standardTraitName(name)
            in
                match state
                |> stateTraitEnvironment
                |> resolveTraitBinding(traitName) with
                    | Some(_known) -> state
                    | None ->
                        match assignTypeParameterIds(typeParameters)(state.typeSupply) with
                            | (namedIds, nextSupply) ->
                                let parameterTypes = typeParameterSemVars(namedIds)
                                in
                                    let table = typeParameterResolutionTable(namedIds)
                                    in
                                        let aliases = stateTypeAliases(state)
                                        in
                                            (state with typeSupply = nextSupply) |> withStateTraitEnvironment(state
                                            |> stateTraitEnvironment
                                            |> addTraitBinding(traitName)(length(typeParameters))(parameterTypes)(userTraitMethodDefinitions(aliases)(traitName)(parameterTypes)(typeParameterQuantified(namedIds))(table)(methods))(userTraitSupertraits(aliases)(table)(supertraits)))

let recursive registerTraitDeclarations (items: List(TopLevelItem)) (state: CoreLoweringState) =
    match items with
        | [] -> state
        | TopLevelAt(_span, inner) :: rest -> registerTraitDeclarations(inner :: rest)(state)
        | TopLevelTrait(declaration) :: rest ->
            state
            |> registerProgramTraitDeclaration(declaration)
            |> registerTraitDeclarations(rest)
        | _other :: rest -> registerTraitDeclarations(rest)(state)

let registerImplementationDeclaration (declaration: TraitImplementationDecl) (state: CoreLoweringState) =
    match declaration with
        | TraitImplementationDecl { traitName = traitName, typeArguments = typeArguments, requirements = requirements, bindings = bindings } ->
            let parameters =
                implementationParameterTypes([]
                |> implementationHeadParameterNamesOf(typeArguments)
                |> reverse)(0)
            in
                match (implementationTypes(stateTypeAliases(state))(parameters)(typeArguments), implementationRequirementConstraints(stateTypeAliases(state))(parameters)(requirements)) with
                    | (Some(headTypes), Some(constraints)) ->
                        state
                        |> withStateTraitEnvironment(state
                        |> stateTraitEnvironment
                        |> withoutSeededImplementation(standardTraitName(traitName))(headTypes)
                        |> addTraitImplementation(standardTraitName(traitName))(headTypes)(constraints)(implementationMethodDefinitions(bindings)))
                        |> Ok
                    | _ -> Error(UnsupportedCoreImplementationHead(traitName))

let recursive registerImplementationDeclarations (declarations: List(TraitImplementationDecl)) (state: CoreLoweringState) =
    match declarations with
        | [] -> Ok(state)
        | declaration :: rest ->
            match registerImplementationDeclaration(declaration)(state) with
                | Error(error) -> Error(error)
                | Ok(registered) -> registerImplementationDeclarations(rest)(registered)

let recursive implementationDeclarationsOf (items: List(TopLevelItem)) (reversed: List(TraitImplementationDecl)) =
    match items with
        | [] -> reverse(reversed)
        | TopLevelAt(_span, inner) :: rest -> implementationDeclarationsOf(inner :: rest)(reversed)
        | TopLevelImplementation(declaration) :: rest -> implementationDeclarationsOf(rest)(declaration :: reversed)
        | _other :: rest -> implementationDeclarationsOf(rest)(reversed)

// Stage 0 registers a program's `implement` declarations, and the ones `deriving` expands to,
// before it lowers any value: each head with its type parameters, its requirements, and its
// method bodies joins the standard trait environment the trait-mapped operators resolve
// against. A program lowered with recorded node types arrives with `deriving` already expanded.
let registerProgramImplementations (items: List(TopLevelItem)) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | Some(_table) ->
            registerImplementationDeclarations(implementationDeclarationsOf(items)([]))(state)
        | None ->
            match expandDerivedImplementations(ProgramSyntax(items = items, body = None)) with
                | Error(error) ->
                    error
                    |> Ashes.Trait.Show.show
                    |> UnsupportedCoreDerivingExpansion
                    |> Error
                | Ok(ProgramSyntax { items = expanded }) ->
                    registerImplementationDeclarations(implementationDeclarationsOf(expanded)([]))(state)

let lowerProgramWithCapabilities items trailingBody environment state =
    match registerProgramCapabilities(items)(state) with
        | Error(error) -> failure(state)(error)
        | Ok(registered) ->
            match registerProgramTypes(items)(registered) with
                | Error(error) -> failure(registered)(error)
                | Ok(typed) ->
                    match typed
                    |> registerProgramProviders(items)
                    |> registerProgramExternals(items) with
                        | Error(error) -> failure(typed)(error)
                        | Ok(withExternals) ->
                            match withExternals
                            |> registerTraitDeclarations(items)
                            |> registerProgramImplementations(items) with
                                | Error(error) -> failure(withExternals)(error)
                                | Ok(withImplementations) ->
                                    withImplementations
                                    |> ensureResultRcEligibility
                                    |> recordContractRecordFacts
                                    |> recordGeneralRcTypeFacts
                                    |> recordCoverageOpenSchemes
                                    |> recordCoverageOperatorConstrainedNames
                                    |> lowerCoreProgramItems(items)(trailingBody)(Ashes.Collection.Map.empty)(environment)(programLoweringAnalysis(items)(trailingBody))([])

// The state after a program's capabilities, types, and externals are registered, before any value
// is lowered: what lowering knows of the program's named types.
let registeredProgramState items state =
    match registerProgramCapabilities(items)(state) with
        | Error(error) -> Error(error)
        | Ok(registered) ->
            match registerProgramTypes(items)(registered) with
                | Error(error) -> Error(error)
                | Ok(typed) -> registerProgramExternals(items)(typed)

// Pending goals are stored newest first. Prepending each resolved occurrence leaves
// each site's list in lowering order, including holes for occurrences that stayed open.
let recursive resolvedTraitGoals (pending: List((Str, SemanticType, List((Str, SemanticType))))) (state: CoreLoweringState) (sites: MapTree(Str, List(Maybe(CoreTraitGoalAnswer)))) =
    match pending with
        | [] -> sites
        | (key, goalType, candidates) :: rest ->
            let resolved = resolveType(state)(goalType)
            in
                let proof =
                    match freeTypeVariables(resolved) with
                        | [] -> Some(ProvenOperandType(resolved))
                        | _ -> resolvedAmbientAnswer(resolved)(candidates)(state)
                in
                    let later =
                        match Ashes.Collection.Map.getStr(key)(sites) with
                            | Some(entries) -> entries
                            | None -> []
                    in
                        sites
                        |> Ashes.Collection.Map.setStr(key)(proof :: later)
                        |> resolvedTraitGoals(rest)(state)

// A site that proved just one type uses it on every visit. Only sites that actually
// proved different types consume one entry per visit, as in a shared implementation
// body lowered for several different dictionaries.
// A lexical dictionary belongs to its original visit, even when neighboring
// visits at the same span did not prove a dictionary.
let recursive traitGoalProofForSite entries first all =
    match (entries, first) with
        | ([], None) -> None
        | ([], Some(ProvenAmbientMethod(_binding))) -> Some(OrderedTraitGoalProofs(all))
        | ([], Some(proven)) -> Some(SharedTraitGoalProof(proven))
        | (None :: rest, _) -> traitGoalProofForSite(rest)(first)(all)
        | (Some(proven) :: rest, None) -> traitGoalProofForSite(rest)(Some(proven))(all)
        | (Some(proven) :: rest, Some(earlier)) ->
            if proven == earlier
            then traitGoalProofForSite(rest)(first)(all)
            else Some(OrderedTraitGoalProofs(all))

let traitGoalMap sites equalityGoals =
    (let initial =
        Ashes.Collection.List.foldLeft(given (proofs) ->
            given (entry) ->
                match entry with
                    | (key, value) -> Ashes.Collection.Map.setStr(key)(SharedTraitGoalProof(ProvenOperandType(value)))(proofs))(Ashes.Collection.Map.empty)(equalityGoals)
    in
        Ashes.Collection.Map.foldLeft(given (acc) ->
            given (key) ->
                given (entries) ->
                    match (acc, traitGoalProofForSite(entries)(None)(entries)) with
                        | ((proofs, ordered), None) -> (proofs, ordered)
                        | ((proofs, _ordered), Some(OrderedTraitGoalProofs(_entries) as proof)) -> (Ashes.Collection.Map.setStr(key)(proof)(proofs), true)
                        | ((proofs, ordered), Some(proof)) -> (Ashes.Collection.Map.setStr(key)(proof)(proofs), ordered))((initial, false))(sites))

// A successful discovery pass validated every body. Final emission only needs
// lambda bodies reachable from executed code or an escaping value. References inside
// an unused lambda are dependencies, not roots; otherwise unused wrapper chains keep
// their abstract Ord goals alive. Reading an earlier proven function binding is also
// pure, so aliases of those lambdas can join the same dependency graph.
type ReplayDependencyGraph =
    | candidates: MapTree(Str, NonCalleeUses)
    | declared: MapTree(Str, Bool)
    | roots: NonCalleeUses

let emptyReplayUses unit = NonCalleeUses(names = Ashes.Collection.Map.empty, everyName = false, includeCallees = true)

let replayLambdaOrAlias value (graph: ReplayDependencyGraph) =
    match stripExprAt(value) with
        | ExprLambda(_parameter, _body, _annotation) -> true
        | ExprVar(name) ->
            match Ashes.Collection.Map.getStr(name)(graph.candidates) with
                | Some(_candidate) -> true
                | None -> false
        | _ -> false

let declareReplayName name (graph: ReplayDependencyGraph) =
    match Ashes.Collection.Map.getStr(name)(graph.declared) with
        | Some(_duplicate) -> graph with roots = (graph.roots with everyName = true)
        | None -> graph with declared = Ashes.Collection.Map.setStr(name)(true)(graph.declared)

let recursive declareReplayGroup bindings graph =
    match bindings with
        | [] -> graph
        | LetBindingSyntax { name = name } :: rest ->
            graph
            |> declareReplayName(name)
            |> declareReplayGroup(rest)

let addReplayCandidate name value (graph: ReplayDependencyGraph) =
    (let dependencies =
        Unit
        |> emptyReplayUses
        |> collectNonCalleeUses(value)(false)([])
    in
        let roots = graph.roots
        in graph with candidates = Ashes.Collection.Map.setStr(name)(dependencies)(graph.candidates), roots = (roots with everyName = roots.everyName || dependencies.everyName))

// One syntax walk builds the dependency maps. Recursive groups and every initializer
// that is not a syntactic lambda or a proven function alias stay eager roots. The
// existing all-reference walker includes methods, providers, exports and qualifiers;
// unknown forms and duplicate declarations conservatively disable omission.
let recursive buildReplayDependencyGraph items (graph: ReplayDependencyGraph) =
    match items with
        | [] -> graph
        | TopLevelAt(_span, inner) :: rest -> buildReplayDependencyGraph(inner :: rest)(graph)
        | TopLevelLet(LetBindingSyntax { name = name, value = value }, false) :: rest ->
            let declared = declareReplayName(name)(graph)
            in
                buildReplayDependencyGraph(rest)((if replayLambdaOrAlias(value)(graph)
                then addReplayCandidate(name)(value)(declared)
                else declared with roots = collectNonCalleeUses(value)(false)([])(declared.roots)))
        | TopLevelLet(LetBindingSyntax { name = name, value = value }, true) :: rest ->
            let declared = declareReplayName(name)(graph)
            in buildReplayDependencyGraph(rest)((declared with roots = collectNonCalleeUses(value)(false)([])(declared.roots)))
        | TopLevelRecursiveGroup(bindings) :: rest ->
            let declared = declareReplayGroup(bindings)(graph)
            in buildReplayDependencyGraph(rest)((declared with roots = letBindingValuesNonCalleeUses(bindings)(declared.roots)))
        | item :: rest ->
            let roots = programNonCalleeUses([item])(ExprBool(true))(graph.roots)
            in buildReplayDependencyGraph(rest)((graph with roots = roots))

let enqueueReplayReferences names pending =
    Ashes.Collection.Map.foldLeft(given (rest) ->
        given (name) ->
            given (_used) -> name :: rest)(pending)(names)

// Each declaration is expanded at most once; repeated edges cost map lookups, not
// repeated AST walks or repeated whole-program pruning passes.
let recursive reachableReplayNames candidates pending visited =
    match pending with
        | [] -> visited
        | name :: rest ->
            match Ashes.Collection.Map.getStr(name)(visited) with
                | Some(_seen) -> reachableReplayNames(candidates)(rest)(visited)
                | None ->
                    let nextVisited = Ashes.Collection.Map.setStr(name)(true)(visited)
                    in
                        match Ashes.Collection.Map.getStr(name)(candidates) with
                            | None -> reachableReplayNames(candidates)(rest)(nextVisited)
                            | Some(NonCalleeUses { names = dependencies }) ->
                                reachableReplayNames(candidates)(enqueueReplayReferences(dependencies)(rest))(nextVisited)

let recursive unreachableReplayItem candidates reachable item =
    match item with
        | TopLevelAt(_span, inner) -> unreachableReplayItem(candidates)(reachable)(inner)
        | TopLevelLet(LetBindingSyntax { name = name }, false) ->
            match Ashes.Collection.Map.getStr(name)(candidates) with
                | Some(_candidate) ->
                    match Ashes.Collection.Map.getStr(name)(reachable) with
                        | Some(_reachable) -> false
                        | None -> true
                | None -> false
        | _ -> false

let recursive retainReachableReplayItems items candidates reachable reversed =
    match items with
        | [] -> reverse(reversed)
        | item :: rest ->
            if unreachableReplayItem(candidates)(reachable)(item)
            then retainReachableReplayItems(rest)(candidates)(reachable)(reversed)
            else retainReachableReplayItems(rest)(candidates)(reachable)(item :: reversed)

let validatedReplayItems items trailingBody =
    (let graph = buildReplayDependencyGraph(items)(ReplayDependencyGraph(candidates = Ashes.Collection.Map.empty, declared = Ashes.Collection.Map.empty, roots = emptyReplayUses(Unit)))
    in
        let roots = collectNonCalleeUses(trailingBody)(false)([])(graph.roots)
        in
            if roots.everyName
            then items
            else
                retainReachableReplayItems(items)(graph.candidates)(reachableReplayNames(graph.candidates)(enqueueReplayReferences(roots.names)([]))(Ashes.Collection.Map.empty))([]))

// Whether `==` at a closed operand type is anything but the comparisons a sealed speculative
// site already becomes: the integer comparison itself, or the string, float and big integer
// forms the finalization rewrite swaps in.
let equalityComparesStructurally (semanticType: SemanticType) =
    match semanticType with
        | SemInt -> false
        | SemUInt(_bits) -> false
        | SemRune -> false
        | SemBool -> false
        | SemFloat -> false
        | SemBigInt -> false
        | SemString -> false
        | _ -> true

let equalityGoalProof (goalType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(goalType)
    in
        match freeTypeVariables(resolved) with
            | [] ->
                if equalityComparesStructurally(resolved)
                then Some(resolved)
                else None
            | _variables -> None)

let sameEqualityGoalProof (left: Maybe(SemanticType)) (right: Maybe(SemanticType)) =
    match (left, right) with
        | (Some(leftType), Some(rightType)) -> leftType == rightType
        | (None, None) -> true
        | _ -> false

// What a pass settled for each speculative equality site: the closed type its operands resolved
// to when the integer comparison does not compare it, and nothing for a site every lowering of
// which did not agree on one such type (a body lowered at several types, or two comparisons
// under one span).
let recursive equalityGoalProofs (goals: List((Str, SemanticType))) (state: CoreLoweringState) (proofs: MapTree(Str, Maybe(SemanticType))) =
    match goals with
        | [] -> proofs
        | (key, goalType) :: rest ->
            let proof = equalityGoalProof(goalType)(state)
            in
                match Ashes.Collection.Map.getStr(key)(proofs) with
                    | None ->
                        proofs
                        |> Ashes.Collection.Map.setStr(key)(proof)
                        |> equalityGoalProofs(rest)(state)
                    | Some(earlier) ->
                        if sameEqualityGoalProof(earlier)(proof)
                        then equalityGoalProofs(rest)(state)(proofs)
                        else
                            proofs
                            |> Ashes.Collection.Map.setStr(key)(None)
                            |> equalityGoalProofs(rest)(state)

let recursive provenEqualityGoals (goals: List((Str, SemanticType))) (proofs: MapTree(Str, Maybe(SemanticType))) (proven: List((Str, SemanticType))) =
    match goals with
        | [] -> proven
        | (key, _goalType) :: rest ->
            match Ashes.Collection.Map.getStr(key)(proofs) with
                | Some(Some(proof)) -> provenEqualityGoals(rest)(proofs)((key, proof) :: proven)
                | _ -> provenEqualityGoals(rest)(proofs)(proven)

// The speculative equality sites of a finished pass that sealed to a wrong comparison, with the
// type to pin at each.
let structuralEqualityGoals (state: CoreLoweringState) =
    (let goals = statePendingEqualityGoals(state)
    in
        provenEqualityGoals(goals)(equalityGoalProofs(goals)(state)(Ashes.Collection.Map.empty))([]))

// Stage 0's discovery pass, run only for a program that needs one: a pass that met a trait goal
// its substitution could not yet answer lowers the program a second time with what that pass went
// on to prove, so each such site chooses the evidence the rest of the program settled on. The
// first pass's own state is a value, so the second starts from the same seed rather than from
// anything the first left behind. Its result is never the program's: a deferred site holds a
// constant standing in for a dispatch, so every pass that deferred one is followed by a pass that
// either dispatches it or reports it. A speculative `==` whose operands the pass went on to
// resolve to a type the integer comparison does not compare is such a site too.
// A goal key's trait: the third of its colon-separated parts.
let traitOfGoalKey (key: Str) =
    match Ashes.Text.split(key)(":") with
        | _start :: _end :: traitName :: _rest -> traitName
        | _parts -> key

// The final pass's result, refused when a dictionary it deferred was never built: the call still
// holds the constant standing in for it.
let refuseUnansweredGoals (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, error = None } ->
            match statePendingTraitGoals(state) with
                | [] -> lowered
                | (key, goalType, _candidates) :: _rest ->
                    goalType
                    |> resolveType(state)
                    |> MissingCoreTraitEvidence(traitOfGoalKey(key))
                    |> failure(state)
        | failed -> failed

let emitProgramWithProofs items trailingBody environment proofs seed =
    seed
    |> withStateProvenTraitGoals(proofs)
    |> withStateTraitGoalsPinned(true)
    |> lowerProgramWithCapabilities(items)(trailingBody)(environment)
    |> refuseUnansweredGoals

let observedRelowerings state =
    match stateTypeObservation(state) with
        | Some(observation) -> observation.relowerings
        | None -> 0

let replayProgramWithProofs items trailingBody environment seed firstState firstError pending equalityGoals =
    match traitGoalMap(resolvedTraitGoals(pending)(firstState)(Ashes.Collection.Map.empty))(equalityGoals) with
        | (proofs, ordered) ->
            let replayItems =
                match firstError with
                    | None -> validatedReplayItems(items)(trailingBody)
                    | Some(_error) -> items
            in
                // A pruned body could have consumed an occurrence of a shared site. Keep
                // unused bodies out of emission, and number the retained traversal afresh.
                if ordered && length(replayItems) != length(items)
                then
                    match lowerProgramWithCapabilities(replayItems)(trailingBody)(environment)(seed) with
                        | LoweredCoreValue { state = retained } ->
                            match retained
                            |> structuralEqualityGoals
                            |> traitGoalMap(resolvedTraitGoals(statePendingTraitGoals(retained))(retained)(Ashes.Collection.Map.empty)) with
                                | (retainedProofs, _ordered) ->
                                    (retained
                                    |> observedRelowerings
                                    |> Some, emitProgramWithProofs(replayItems)(trailingBody)(environment)(retainedProofs)(seed))
                else (None, emitProgramWithProofs(replayItems)(trailingBody)(environment)(proofs)(seed))

let lowerScheduledProgramPasses items trailingBody environment (seed: CoreLoweringState) =
    match seed |> lowerProgramWithCapabilities(items)(trailingBody)(environment) with
        | LoweredCoreValue { state = firstState, error = firstError } as first ->
            match (statePendingTraitGoals(firstState), structuralEqualityGoals(firstState)) with
                | ([], []) -> first
                | (pending, equalityGoals) ->
                    match replayProgramWithProofs(items)(trailingBody)(environment)(seed)(firstState)(firstError)(pending)(equalityGoals) with
                        | (_retained, emitted) -> emitted

let lowerProgramPasses items trailing environment seed =
    match scheduleStrictProgram(stateNodeTypes(seed))(items)(trailing) with
        | (scheduled, body) -> lowerScheduledProgramPasses(scheduled)(body)(environment)(seed)

// The two passes lowerProgramPasses runs, each with the time `clock` reads around it in
// milliseconds, and how many trait goals and structural equality goals the first left open.
type CoreProgramPassMeasurement =
    | first: LoweredCoreValue
    | replay: Maybe(LoweredCoreValue)
    | firstMillis: Int
    | replayMillis: Int
    | pendingGoals: Int
    | equalityGoals: Int
    | pendingGoalKeys: List(Str)
    | equalityGoalKeys: List(Str)
    | retainedRelowerings: Maybe(Int)
    | goalDetails: List((ObservedPendingGoal, SemanticType))

// The site keys of a pass's pending trait goals.
let recursive goalKeysOf (goals: List((Str, SemanticType))) =
    match goals with
        | [] -> []
        | (key, _goalType) :: rest -> key :: goalKeysOf(rest)

let recursive indexGoalKeys goals keys =
    match goals with
        | [] -> keys
        | (key, _operand) :: rest ->
            keys
            |> Ashes.Collection.Map.setStr(key)(true)
            |> indexGoalKeys(rest)

let recursive measuredSites sites deferred equalities state =
    match sites with
        | [] -> []
        | (ObservedPendingGoal { key = key, structural = structural, operand = operand } as site) :: rest ->
            let keys =
                if structural
                then equalities
                else deferred
            in
                match Ashes.Collection.Map.getStr(key)(keys) with
                    | None -> measuredSites(rest)(deferred)(equalities)(state)
                    | Some(_) -> (site, resolveType(state)(operand)) :: measuredSites(rest)(deferred)(equalities)(state)

let measuredGoalDetails pending equalityGoals state =
    match stateTypeObservation(state) with
        | Some(CoreTypeObservation { goalSites = Some(sites) }) ->
            measuredSites(sites)(indexGoalKeys(pending)(Ashes.Collection.Map.empty))(indexGoalKeys(equalityGoals)(Ashes.Collection.Map.empty))(state)
        | _ -> []

let pendingGoalPairs goals =
    Ashes.Collection.List.map(given (goal) ->
        match goal with
            | (key, operand, _candidates) -> (key, operand))(goals)

let lowerScheduledProgramPassesMeasured replayStarting clock items trailingBody environment (seed: CoreLoweringState) =
    (let started = clock(Unit)
    in
        match seed |> lowerProgramWithCapabilities(items)(trailingBody)(environment) with
            | LoweredCoreValue { state = firstState, error = firstError } as first ->
                let firstDone = clock(Unit)
                in
                    match (statePendingTraitGoals(firstState), structuralEqualityGoals(firstState)) with
                        | ([], []) -> CoreProgramPassMeasurement(first = first, replay = None, firstMillis = firstDone - started, replayMillis = 0, pendingGoals = 0, equalityGoals = 0, pendingGoalKeys = [], equalityGoalKeys = [], retainedRelowerings = None, goalDetails = [])
                        | (pending, equalityGoals) ->
                            let beforeProgress = clock(Unit)
                            in
                                let _ = replayStarting(Unit)
                                in
                                    let afterProgress = clock(Unit)
                                    in
                                        match replayProgramWithProofs(items)(trailingBody)(environment)(seed)(firstState)(firstError)(pending)(equalityGoals) with
                                            | (retained, replayed) ->
                                                let elapsed = beforeProgress - firstDone + clock(Unit) - afterProgress
                                                in
                                                    let goalPairs = pendingGoalPairs(pending)
                                                    in CoreProgramPassMeasurement(first = first, replay = Some(replayed), firstMillis = firstDone - started, replayMillis = elapsed, pendingGoals = Ashes.Collection.List.length(pending), equalityGoals = Ashes.Collection.List.length(equalityGoals), pendingGoalKeys = goalPairs |> goalKeysOf, equalityGoalKeys = goalKeysOf(equalityGoals), retainedRelowerings = retained, goalDetails = measuredGoalDetails(goalPairs)(equalityGoals)(firstState)))

let lowerProgramPassesMeasuredWithProgress replayStarting clock items trailing environment seed =
    match scheduleStrictProgram(stateNodeTypes(seed))(items)(trailing) with
        | (scheduled, body) -> lowerScheduledProgramPassesMeasured(replayStarting)(clock)(scheduled)(body)(environment)(seed)

let lowerProgramPassesMeasured clock items trailing environment seed =
    lowerProgramPassesMeasuredWithProgress(given (_) -> Unit)(clock)(items)(trailing)(environment)(seed)

// The label ids the deferred reset blocks are numbered with as the whole program is lowered:
// the next id past the program's own and the base each group was given, by its range start.
type DeferredLabelNumbering =
    | nextLabelId: Int
    | bases: List((Int, Int))

let recursive lastTextPiece (pieces: List(Str)) =
    match pieces with
        | [] -> ""
        | piece :: [] -> piece
        | _piece :: rest -> lastTextPiece(rest)

// The id of a label allocated from the deferred range, read off its numeric suffix.
let recursive allDigits (text: Str) =
    match Ashes.Text.unconsText(text) with
        | None -> true
        | Some((digit, rest)) -> Ashes.Text.isDigitText(digit) && allDigits(rest)

let deferredLabelId (name: Str) =
    match "_"
    |> Ashes.Text.split(name)
    |> lastTextPiece with
        | "" -> None
        | digits ->
            if allDigits(digits)
            then
                let id = parseDecimalDigits(digits)(0)
                in
                    if id >= deferredLabelBase
                    then Some(id)
                    else None
            else None

let recursive deferredGroupOf (id: Int) (groups: List((Int, Int))) =
    match groups with
        | [] -> None
        | (start, count) :: rest ->
            if id >= start && id < start + count
            then Some((start, count))
            else deferredGroupOf(id)(rest)

let recursive lookupDeferredGroupBase (start: Int) (bases: List((Int, Int))) =
    match bases with
        | [] -> None
        | (candidate, base) :: rest ->
            if candidate == start
            then Some(base)
            else lookupDeferredGroupBase(start)(rest)

// A deferred label's stage 0 name: its group takes the next ids when first met in stream
// order, and every label of the group keeps its offset within the range.
let numberDeferredLabel (name: Str) (groups: List((Int, Int))) (numbering: DeferredLabelNumbering) =
    match deferredLabelId(name) with
        | None -> (name, numbering)
        | Some(id) ->
            match deferredGroupOf(id)(groups) with
                | None -> (name, numbering)
                | Some((start, count)) ->
                    let prefix =
                        Ashes.Text.take(name)(Ashes.Text.length(name) - Ashes.Text.length(Ashes.Text.fromInt(id)) - 1)
                    in
                        match lookupDeferredGroupBase(start)(numbering.bases) with
                            | Some(base) -> (prefix + "_" + Ashes.Text.fromInt(base + id - start), numbering)
                            | None -> (prefix + "_" + Ashes.Text.fromInt(numbering.nextLabelId + id - start), DeferredLabelNumbering(nextLabelId = numbering.nextLabelId + count, bases = (start, numbering.nextLabelId) :: numbering.bases))

let recursive numberSwitchCaseLabels (cases: List(IrSwitchCase)) (groups: List((Int, Int))) (numbering: DeferredLabelNumbering) (reversed: List(IrSwitchCase)) =
    match cases with
        | [] -> (reverse(reversed), numbering)
        | (IrSwitchCase { label = label } as switchCase) :: rest ->
            match numberDeferredLabel(label)(groups)(numbering) with
                | (numbered, next) -> numberSwitchCaseLabels(rest)(groups)(next)((switchCase with label = numbered) :: reversed)

let numberInstructionLabels (instruction: IrInstruction) (groups: List((Int, Int))) (numbering: DeferredLabelNumbering) =
    match instruction.instruction with
        | Label(name) ->
            match numberDeferredLabel(name)(groups)(numbering) with
                | (numbered, next) -> ((instruction with instruction = Label(numbered)), next)
        | Jump(name) ->
            match numberDeferredLabel(name)(groups)(numbering) with
                | (numbered, next) -> ((instruction with instruction = Jump(numbered)), next)
        | JumpIfFalse(condition, name) ->
            match numberDeferredLabel(name)(groups)(numbering) with
                | (numbered, next) -> ((instruction with instruction = JumpIfFalse(condition)(numbered)), next)
        | SwitchTag(tagTemp, cases, defaultLabel) ->
            match numberSwitchCaseLabels(cases)(groups)(numbering)([]) with
                | (numberedCases, afterCases) ->
                    match numberDeferredLabel(defaultLabel)(groups)(afterCases) with
                        | (numberedDefault, next) -> ((instruction with instruction = SwitchTag(tagTemp)(numberedCases)(numberedDefault)), next)
        | _ -> (instruction, numbering)

let recursive numberInstructionsLabels (instructions: List(IrInstruction)) (groups: List((Int, Int))) (numbering: DeferredLabelNumbering) (reversed: List(IrInstruction)) =
    match instructions with
        | [] -> (reverse(reversed), numbering)
        | instruction :: rest ->
            match numberInstructionLabels(instruction)(groups)(numbering) with
                | (numbered, next) -> numberInstructionsLabels(rest)(groups)(next)(numbered :: reversed)

let recursive numberFunctionsLabels (functions: List(IrFunction)) (groups: List((Int, Int))) (numbering: DeferredLabelNumbering) (reversed: List(IrFunction)) =
    match functions with
        | [] -> (reverse(reversed), numbering)
        | function_ :: rest ->
            match numberInstructionsLabels(function_.instructions)(groups)(numbering)([]) with
                | (numbered, next) -> numberFunctionsLabels(rest)(groups)(next)((function_ with instructions = numbered) :: reversed)

// Stage 0's `ResolveDeferredTcoResets` numbering: the reset blocks' labels take the ids past the
// program's own, the entry's blocks first and then the lifted functions' in order.
let numberDeferredLabels (state: CoreLoweringState) (entryInstructions: List(IrInstruction)) (functions: List(IrFunction)) =
    match stateDeferredLabelGroups(state) with
        | [] -> (entryInstructions, functions)
        | groups ->
            match numberInstructionsLabels(entryInstructions)(groups)(DeferredLabelNumbering(nextLabelId = state.nextLabelId, bases = []))([]) with
                | (numberedEntry, numbering) ->
                    match numberFunctionsLabels(functions)(groups)(numbering)([]) with
                        | (numberedFunctions, _final) -> (numberedEntry, numberedFunctions)

// Stage 0's `MarkGeneralRcOwnedResultClosures`: once the whole program is lowered, every closure over
// a function that normalizes its result at its return says so in its header, a recursive function's
// own closure (built before its body was finished) included, so a caller that cannot name the callee
// takes such a result over owned instead of retaining it.
let markOwnedResultClosure (labels: List(Str)) (instruction: IrInstruction) =
    match instruction with
        | IrInstruction { instruction = MakeClosure(target, funcLabel, environmentTemp, environmentSize, runtimeManaged, returnsRuntimeManaged, acceptsRuntimeManagedArgument, false), location = location } ->
            if containsLabel(funcLabel)(labels)
            then IrInstruction(instruction = MakeClosure(target)(funcLabel)(environmentTemp)(environmentSize)(runtimeManaged)(returnsRuntimeManaged)(acceptsRuntimeManagedArgument)(true), location = location)
            else instruction
        | IrInstruction { instruction = MakeClosureStack(target, funcLabel, environmentTemp, environmentSize, returnsRuntimeManaged, acceptsRuntimeManagedArgument, false), location = location } ->
            if containsLabel(funcLabel)(labels)
            then IrInstruction(instruction = MakeClosureStack(target)(funcLabel)(environmentTemp)(environmentSize)(returnsRuntimeManaged)(acceptsRuntimeManagedArgument)(true), location = location)
            else instruction
        | _ -> instruction

let markOwnedResultClosures (labels: List(Str)) (functions: List(IrFunction)) =
    map((given (function_: IrFunction) ->
        function_ with instructions = map(markOwnedResultClosure(labels))(function_.instructions)))(functions)

let recursive allLayoutsNullary (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> true
        | layout :: rest -> constructorArity(layout) == 0 && allLayoutsNullary(rest)

let declaredAllNullary (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> false
        | _ -> allLayoutsNullary(layouts)

// Stage 0's `ReleaseEntryGeneralRcOwnedSlots`: the program's entry holds owned call results like
// any function (a fresh argument of the trailing pipe, say) and releases them at its end, as a
// function does at its return, when its value cannot point into them: a scalar, or a value of a
// type whose constructors all carry nothing. The releases carry no source location, as stage 0 emits
// them once the entry's own expression is lowered.
let entryValueHoldsNoHeap (semanticType: SemanticType) (state: CoreLoweringState) =
    resultSurvivesReset(semanticType)(state) || (match resolveType(state)(semanticType) with
        | SemNamed(_symbolId, name, _arguments) ->
            state
            |> stateConstructorsOfType(name)
            |> declaredAllNullary
        | _ -> false)

let emitUnlocatedGeneralRcOwnedSlotDrops (state: CoreLoweringState) =
    (let released = emitGeneralRcOwnedSlotDrops((state with currentSpan = None))
    in released with currentSpan = state.currentSpan)

let releaseEntryGeneralRcOwnedSlots (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, semanticType = semanticType, error = None } ->
            if entryValueHoldsNoHeap(semanticType)(state)
            then lowered with state = emitUnlocatedGeneralRcOwnedSlotDrops(state)
            else lowered
        | _ -> lowered

let buildProgram lowered =
    match lowered
    |> refuseUnansweredGoals
    |> releaseEntryGeneralRcOwnedSlots
    |> resolveLoweredPendingBlocks with
        | LoweredCoreValue { error = Some(error) } -> failedCoreLowering(error)
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            match state with
                | CoreLoweringState { reversedInstructions = instructions, functions = functions, externalFunctions = externalFunctions, externalOpaqueTypes = externalOpaqueTypes, nextLocal = localCount, nextTemp = tempCount, stringLiterals = stringLiterals, functionState = CoreFunctionState { pendingOperatorDefaults = pendingOperatorDefaults, sealedOperatorDefaults = sealedOperatorDefaults } } ->
                    match applyDeferredOperators(state)(pendingOperatorDefaults)((entryInstructions(temp)(instructions), tempCount)) with
                        | (deferredEntryInstructions, entryTempCount) ->
                            match functions
                            |> reverse
                            |> applySealedDeferredOperators(state)(sealedOperatorDefaults)
                            |> insertClosureNormalizers(state
                            |> statePendingClosureNormalizers
                            |> reverse)(operatorDefaultedVariables([]
                            |> sealedOperatorTypes(sealedOperatorDefaults)
                            |> append(pendingOperatorTypes(pendingOperatorDefaults)([])))(state))(state)
                            |> numberDeferredLabels(state)(deferredEntryInstructions)
                            |> (given (numbered: (List(IrInstruction), List(IrFunction))) ->
                                match numbered with
                                    | (numberedEntry, numberedFunctions) ->
                                        (map(state
                                        |> stateGeneralRcOwnedResultLabels
                                        |> markOwnedResultClosure)(numberedEntry), markOwnedResultClosures(stateGeneralRcOwnedResultLabels(state))(numberedFunctions))) with
                                | (resolvedEntryInstructions, resolvedFunctions) ->
                                    let entry =
                                        IrFunction(
                                            label = "_start_main",
                                            instructions = resolvedEntryInstructions,
                                            localCount = localCount,
                                            tempCount = entryTempCount,
                                            hasEnvAndArgParams = false,
                                            coroutine = None,
                                            localNames = [],
                                            localTypes = [],
                                            origin = Some(entryOrigin),
                                            lifetimesPlaced = false
                                        )
                                    in
                                        match collectCoreFunctionUses(
                                            resolvedFunctions
                                        )(
                                            collectCoreInstructionUses(resolvedEntryInstructions)(emptyCoreProgramUses)
                                        ) with
                                            | CoreProgramUses { printInt = usesPrintInt, printStr = usesPrintStr, printBool = usesPrintBool, concatStr = usesConcatStr } ->
                                                CoreLoweringResult(
                                                    program = IrProgram(
                                                        entryFunction = entry,
                                                        functions = resolvedFunctions,
                                                        stringLiterals = stringLiterals,
                                                        externalFunctions = externalFunctions,
                                                        externalOpaqueTypes = externalOpaqueTypes,
                                                        usesPrintInt = usesPrintInt,
                                                        usesPrintStr = usesPrintStr,
                                                        usesPrintBool = usesPrintBool,
                                                        usesConcatStr = usesConcatStr,
                                                        usesClosures = hasFunctions(functions),
                                                        usesAsync = false,
                                                        capabilityHandlerGlobals = capabilityHandlerGlobalCount(state),
                                                        traitEvidence = emptyTraitEvidenceAnnotations
                                                    )
                                                    |> placeLifetimes(state.borrowedCalls)
                                                    |> Some,
                                                    semanticType = resolveType(state)(semanticType),
                                                    error = None,
                                                    valuePlacements = state.valuePlacements
                                                    |> dedupeValuePlacements
                                                    |> finalizeValuePlacements(state),
                                                    joinRepresentations = state.joinRepresentations
                                                )

// Seeds the state with the whole-program inspect-only fixpoint over the program's registered
// top-level functions, the verdict `markCallArgumentsMoved` consults for hand-offs.
// The summaries by `name@identity`, the index a call site resolves its callee's recorded lambda
// identity through.
let recursive reachSummariesByIdentity (summaries: List(ReachSummary)) (byIdentity: MapTree(Str, ReachSummary)) =
    match summaries with
        | [] -> byIdentity
        | (ReachSummary { function = ReachFunction { name = name, identity = identity } } as summary) :: rest ->
            byIdentity
            |> Ashes.Collection.Map.setStr(name + "@" + Ashes.Text.fromInt(identity))(summary)
            |> reachSummariesByIdentity(rest)

let isTopLevelSummary (summary: ReachSummary) =
    match summary with
        | ReachSummary { function = ReachFunction { enclosing = None } } -> true
        | _ -> false

// The summaries by name, a top-level function preferred over a nested one and the first
// registered otherwise, the answer `reachSummaryNamed` walked the list for.
let recursive reachSummariesByName (summaries: List(ReachSummary)) (byName: MapTree(Str, ReachSummary)) =
    match summaries with
        | [] -> byName
        | (ReachSummary { function = ReachFunction { name = name } } as summary) :: rest ->
            byName
            |> Ashes.Collection.Map.upsertStr(name)(summary)(given (existing: ReachSummary) ->
                if isTopLevelSummary(existing) || isTopLevelSummary(summary) == false
                then existing
                else summary)
            |> reachSummariesByName(rest)

let recursive callReachByFunctionKey (summaries: List(ReachSummary)) (index: MapTree(Str, PreparedCallReach)) =
    match summaries with
        | [] -> index
        | ReachSummary { function = ReachFunction { key = key, parameters = parameters }, reach = reach } :: rest ->
            index
            |> Ashes.Collection.Map.setStr(key)(prepareCallReach(parameters)(reach))
            |> callReachByFunctionKey(rest)

// Seeds the state with completed summaries and all of their query indexes.
let withReachSummaries (summaries: List(ReachSummary)) (state: CoreLoweringState) =
    state
    |> withStateReachSummaries(summaries)
    |> withStateReachTable(summariesTable(summaries))
    |> withStateReachByIdentity(reachSummariesByIdentity(summaries)(Ashes.Collection.Map.empty))
    |> withStateReachByName(reachSummariesByName(summaries)(Ashes.Collection.Map.empty))
    |> withStateCallReachByKey(callReachByFunctionKey(summaries)(Ashes.Collection.Map.empty))

// Stage 0's live-handler effects (`ComputeLiveHandlerEffects`): what one function's body tells the
// analysis. The functions it calls by name, the functions it names as values (which may escape
// and be called anywhere), whether it installs a handler, and whether it calls a local value,
// a callee the analysis cannot name.
type HandlerEffectScan =
    | callees: List(Str)
    | references: List(Str)
    | containsHandle: Bool
    | dynamicCall: Bool

let emptyHandlerEffectScan = HandlerEffectScan(callees = [], references = [], containsHandle = false, dynamicCall = false)

let isLambdaValue (value: Expr) =
    match unspanArgument(value) with
        | ExprLambda(_, _, _) -> true
        | _ -> false

let bindsLetValue (parameters: List(Str)) (value: Expr) =
    match parameters with
        | [] -> isLambdaValue(value) == false
        | _ -> false

let isProgramFunction (functions: MapTree(Str, Bool)) (name: Str) =
    match Ashes.Collection.Map.getStr(name)(functions) with
        | Some(_) -> true
        | None -> false

let recursive scanHandlerEffects (functions: MapTree(Str, Bool)) (locals: List(Str)) (expression: Expr) (scan: HandlerEffectScan) =
    match expression with
        | ExprAt(_span, inner) -> scanHandlerEffects(functions)(locals)(inner)(scan)
        | ExprNode(_node, inner) -> scanHandlerEffects(functions)(locals)(inner)(scan)
        | ExprVar(name) ->
            if isProgramFunction(functions)(name)
            then scan with references = name :: scan.references
            else scan
        | ExprCall(function, argument, _isSugar, _layout) ->
            scan
            |> scanHandlerEffectCallee(functions)(locals)(function)
            |> scanHandlerEffects(functions)(locals)(argument)
        | ExprLambda(parameter, body, _annotation) -> scanHandlerEffects(functions)(parameter :: locals)(body)(scan)
        | ExprLet(name, value, body, parameters, _annotation, _traits) ->
            scanHandlerEffectLet(functions)(locals)(name)(value)(body)(bindsLetValue(parameters)(value))(scan)
        | ExprLetRecursive(name, value, body, parameters, _annotation, _traits) ->
            scanHandlerEffectLet(functions)(locals)(name)(value)(body)(bindsLetValue(parameters)(value))(scan)
        | ExprLetResult(name, value, body) -> scanHandlerEffectLet(functions)(locals)(name)(value)(body)(true)(scan)
        | ExprIf(condition, thenBranch, elseBranch) ->
            scan
            |> scanHandlerEffects(functions)(locals)(condition)
            |> scanHandlerEffects(functions)(locals)(thenBranch)
            |> scanHandlerEffects(functions)(locals)(elseBranch)
        | ExprMatch(scrutinee, arms, _defaultArm) ->
            scan
            |> scanHandlerEffects(functions)(locals)(scrutinee)
            |> scanHandlerEffectArms(functions)(locals)(arms)
        | ExprHandle(body, arms) ->
            (scan with containsHandle = true)
            |> scanHandlerEffects(functions)(locals)(body)
            |> scanHandlerEffectOperationArms(functions)(locals)(arms)
        | ExprTuple(elements) -> scanHandlerEffectList(functions)(locals)(elements)(scan)
        | ExprList(elements, _isMultiline) -> scanHandlerEffectList(functions)(locals)(elements)(scan)
        | ExprCons(head, tail) -> scanHandlerEffectList(functions)(locals)([head, tail])(scan)
        | ExprRecord(_constructor, fields, _isMultiline) -> scanHandlerEffectFields(functions)(locals)(fields)(scan)
        | ExprRecordUpdate(record, fields) ->
            scan
            |> scanHandlerEffects(functions)(locals)(record)
            |> scanHandlerEffectFields(functions)(locals)(fields)
        | ExprAdd(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprSubtract(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprMultiply(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprDivide(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprModulo(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprBitwiseAnd(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprBitwiseOr(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprBitwiseXor(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprShiftLeft(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprShiftRight(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprLogicalAnd(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprLogicalOr(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprGreaterThan(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprLessThan(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprGreaterOrEqual(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprLessOrEqual(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprEqual(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprNotEqual(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprResultPipe(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprResultMapErrorPipe(left, right) -> scanHandlerEffectList(functions)(locals)([left, right])(scan)
        | ExprBitwiseNot(operand) -> scanHandlerEffects(functions)(locals)(operand)(scan)
        | ExprLogicalNot(operand) -> scanHandlerEffects(functions)(locals)(operand)(scan)
        | ExprAwait(operand) -> scanHandlerEffects(functions)(locals)(operand)(scan)
        | ExprPerform(operand) -> scanHandlerEffects(functions)(locals)(operand)(scan)
        | _ -> scan
// The callee position of a call spine: a program function named there is a call, not an escape,
// and a local value named there is a call the analysis cannot follow.
and scanHandlerEffectCallee (functions: MapTree(Str, Bool)) (locals: List(Str)) (callee: Expr) (scan: HandlerEffectScan) =
    match unspanArgument(callee) with
        | ExprCall(function, argument, _isSugar, _layout) ->
            scan
            |> scanHandlerEffectCallee(functions)(locals)(function)
            |> scanHandlerEffects(functions)(locals)(argument)
        | ExprVar(name) ->
            if containsName(name)(locals)
            then scan with dynamicCall = true
            else
                if isProgramFunction(functions)(name)
                then scan with callees = name :: scan.callees
                else scan
        | other -> scanHandlerEffects(functions)(locals)(other)(scan)
and scanHandlerEffectLet (functions: MapTree(Str, Bool)) (locals: List(Str)) (name: Str) (value: Expr) (body: Expr) (bindsValue: Bool) (scan: HandlerEffectScan) =
    (let bodyLocals =
        if bindsValue
        then name :: locals
        else locals
    in
        scan
        |> scanHandlerEffects(functions)(locals)(value)
        |> scanHandlerEffects(functions)(bodyLocals)(body))
and scanHandlerEffectList (functions: MapTree(Str, Bool)) (locals: List(Str)) (expressions: List(Expr)) (scan: HandlerEffectScan) =
    match expressions with
        | [] -> scan
        | expression :: rest ->
            scan
            |> scanHandlerEffects(functions)(locals)(expression)
            |> scanHandlerEffectList(functions)(locals)(rest)
and scanHandlerEffectFields (functions: MapTree(Str, Bool)) (locals: List(Str)) (fields: List((Str, Expr))) (scan: HandlerEffectScan) =
    match fields with
        | [] -> scan
        | (_field, value) :: rest ->
            scan
            |> scanHandlerEffects(functions)(locals)(value)
            |> scanHandlerEffectFields(functions)(locals)(rest)
and scanHandlerEffectArms (functions: MapTree(Str, Bool)) (locals: List(Str)) (arms: List((Pattern, Expr, Maybe(Expr)))) (scan: HandlerEffectScan) =
    match arms with
        | [] -> scan
        | (pattern, body, guard) :: rest ->
            let armLocals = patternBoundNames(pattern)(locals)
            in
                let guarded =
                    match guard with
                        | Some(condition) -> scanHandlerEffects(functions)(armLocals)(condition)(scan)
                        | None -> scan
                in
                    guarded
                    |> scanHandlerEffects(functions)(armLocals)(body)
                    |> scanHandlerEffectArms(functions)(locals)(rest)
and scanHandlerEffectOperationArms (functions: MapTree(Str, Bool)) (locals: List(Str)) (arms: List((Maybe(Str), Str, List(Pattern), Expr))) (scan: HandlerEffectScan) =
    match arms with
        | [] -> scan
        | (_capability, _operation, patterns, body) :: rest ->
            scan
            |> scanHandlerEffects(functions)(patternsBoundNames(patterns)(locals))(body)
            |> scanHandlerEffectOperationArms(functions)(locals)(rest)

let recursive programFunctionSet (table: List((Str, List(Str), Expr))) (functions: MapTree(Str, Bool)) =
    match table with
        | [] -> functions
        | (_name, [], _body) :: rest -> programFunctionSet(rest)(functions)
        | (name, _parameters, _body) :: rest ->
            functions
            |> Ashes.Collection.Map.setStr(name)(true)
            |> programFunctionSet(rest)

// Each function's scan, and the entry scope's: the trailing expression and every parameterless
// top-level binding's right-hand side, which stage 0 counts as one scope.
let recursive scanProgramHandlerEffects (functions: MapTree(Str, Bool)) (table: List((Str, List(Str), Expr))) (entry: HandlerEffectScan) (scans: List((Str, HandlerEffectScan))) =
    match table with
        | [] -> (entry, scans)
        | (_name, [], body) :: rest ->
            scanProgramHandlerEffects(functions)(rest)(scanHandlerEffects(functions)([])(body)(entry))(scans)
        | (name, parameters, body) :: rest -> scanProgramHandlerEffects(functions)(rest)(entry)((name, scanHandlerEffects(functions)(parameters)(body)(emptyHandlerEffectScan)) :: scans)

let recursive addLiveFunctions (names: List(Str)) (live: MapTree(Str, Bool)) (changed: Bool) =
    match names with
        | [] -> (live, changed)
        | name :: rest ->
            if isProgramFunction(live)(name)
            then addLiveFunctions(rest)(live)(changed)
            else
                addLiveFunctions(rest)(Ashes.Collection.Map.setStr(name)(true)(live))(true)

let recursive seedLiveFunctions (scans: List((Str, HandlerEffectScan))) (live: MapTree(Str, Bool)) =
    match scans with
        | [] -> live
        | (name, scan) :: rest ->
            if scan.containsHandle
            then
                live
                |> Ashes.Collection.Map.setStr(name)(true)
                |> seedLiveFunctions(rest)
            else seedLiveFunctions(rest)(live)

let recursive anyInstallsHandler (scans: List((Str, HandlerEffectScan))) =
    match scans with
        | [] -> false
        | (_name, scan) :: rest -> scan.containsHandle || anyInstallsHandler(rest)

let recursive escapedFunctions (scans: List((Str, HandlerEffectScan))) (escaped: List(Str)) =
    match scans with
        | [] -> escaped
        | (_name, scan) :: rest ->
            escaped
            |> append(scan.references)
            |> escapedFunctions(rest)

// One pass of `PropagateLiveHandlerEffects`: every live function's callees, and every escaped
// function when it calls a value the analysis cannot name.
let recursive propagateLiveFunctions (scans: List((Str, HandlerEffectScan))) (escaped: List(Str)) (live: MapTree(Str, Bool)) (changed: Bool) =
    match scans with
        | [] -> (live, changed)
        | (name, scan) :: rest ->
            if isProgramFunction(live)(name)
            then
                match addLiveFunctions(scan.callees)(live)(changed) with
                    | (withCallees, calleesChanged) ->
                        if scan.dynamicCall
                        then
                            match addLiveFunctions(escaped)(withCallees)(calleesChanged) with
                                | (withEscaped, escapedChanged) -> propagateLiveFunctions(rest)(escaped)(withEscaped)(escapedChanged)
                        else propagateLiveFunctions(rest)(escaped)(withCallees)(calleesChanged)
            else propagateLiveFunctions(rest)(escaped)(live)(changed)

let recursive liveFunctionsFixpoint (scans: List((Str, HandlerEffectScan))) (escaped: List(Str)) (live: MapTree(Str, Bool)) =
    match propagateLiveFunctions(scans)(escaped)(live)(false) with
        | (next, true) -> liveFunctionsFixpoint(scans)(escaped)(next)
        | (next, false) -> next

let entryHandlerEffectScan (functions: MapTree(Str, Bool)) (body: Maybe(Expr)) =
    match body with
        | Some(expression) -> scanHandlerEffects(functions)([])(expression)(emptyHandlerEffectScan)
        | None -> emptyHandlerEffectScan

// The functions the entry scope makes live when it installs a handler itself: its callees, and
// every escaped function when it calls a value the analysis cannot name.
let entryLiveFunctions (entry: HandlerEffectScan) (escaped: List(Str)) (live: MapTree(Str, Bool)) =
    if entry.containsHandle
    then
        match addLiveFunctions(entry.callees)(live)(false) with
            | (withCallees, _changed) ->
                if entry.dynamicCall
                then
                    match addLiveFunctions(escaped)(withCallees)(false) with
                        | (withEscaped, _escapedChanged) -> withEscaped
                else withCallees
    else live

// Stage 0's `ComputeLiveHandlerEffects`: the top-level functions that may run while a one-shot
// handler's post is pending, the least fixpoint from every function installing a handler through
// its callees, and whether the entry scope installs one. A function nested in a top-level one is
// counted as part of it.
let programLiveHandlerFunctions (table: List((Str, List(Str), Expr))) (body: Maybe(Expr)) =
    (let functions = programFunctionSet(table)(Ashes.Collection.Map.empty)
    in
        match scanProgramHandlerEffects(functions)(table)(entryHandlerEffectScan(functions)(body))([]) with
            | (entry, scans) ->
                let escaped = escapedFunctions(scans)(entry.references)
                in
                    Ashes.Collection.Map.empty
                    |> seedLiveFunctions(scans)
                    |> entryLiveFunctions(entry)(escaped)
                    |> liveFunctionsFixpoint(scans)(escaped)
                    |> (given (live) -> (live, entry.containsHandle, entry.containsHandle || anyInstallsHandler(scans))))

// The `Ashes.Task.Parallel` functions whose callback arguments run on worker threads (stage 0's
// `ParallelWorkerCalleeNames`), by qualified and by stitched name.
let parallelWorkerMemberNames = ["map", "reduce", "mapGrained", "reduceGrained"]

let parallelWorkerStitchedNames = ["Ashes_Task_Parallel_map", "Ashes_Task_Parallel_reduce", "Ashes_Task_Parallel_mapGrained", "Ashes_Task_Parallel_reduceGrained"]

let isParallelWorkerCallee (callee: Expr) =
    match unspanArgument(callee) with
        | ExprQualifiedVar(moduleName, memberName) -> moduleName == "Ashes.Task.Parallel" && containsName(memberName)(parallelWorkerMemberNames)
        | ExprVar(name) -> containsName(name)(parallelWorkerStitchedNames)
        | _ -> false

// The root and the arguments of a call spine.
let recursive callSpineParts (expression: Expr) (arguments: List(Expr)) =
    match unspanArgument(expression) with
        | ExprCall(function, argument, _isSugar, _layout) -> callSpineParts(function)(argument :: arguments)
        | root -> (root, arguments)

// Stage 0's `MarkFunctionsReachableFrom` over a worker callback's arguments: the program functions
// they call or name, and every escaped function when one of them calls a value the analysis
// cannot name.
let recursive seedParallelWorkerArguments (functions: MapTree(Str, Bool)) (escaped: List(Str)) (arguments: List(Expr)) (seeds: MapTree(Str, Bool)) =
    match arguments with
        | [] -> seeds
        | argument :: rest ->
            let scan = scanHandlerEffects(functions)([])(argument)(emptyHandlerEffectScan)
            in
                let named = append(scan.callees)(scan.references)
                in
                    let marked =
                        if scan.dynamicCall
                        then append(named)(escaped)
                        else named
                    in
                        match addLiveFunctions(marked)(seeds)(false) with
                            | (added, _changed) -> seedParallelWorkerArguments(functions)(escaped)(rest)(added)

let parallelWorkerSeedVisit (functions: MapTree(Str, Bool)) (escaped: List(Str)) =
    given (seeds: MapTree(Str, Bool)) ->
        given (_id: Int) ->
            given (_span) ->
                given (node: Expr) ->
                    match node with
                        | ExprCall(_function, _argument, _isSugar, _layout) ->
                            match callSpineParts(node)([]) with
                                | (root, arguments) ->
                                    if isParallelWorkerCallee(root)
                                    then seedParallelWorkerArguments(functions)(escaped)(arguments)(seeds)
                                    else seeds
                        | _ -> seeds

let recursive seedParallelWorkerTable (functions: MapTree(Str, Bool)) (escaped: List(Str)) (table: List((Str, List(Str), Expr))) (seeds: MapTree(Str, Bool)) =
    match table with
        | [] -> seeds
        | (_name, _parameters, body) :: rest ->
            body
            |> foldExpressionNodes(parallelWorkerSeedVisit(functions)(escaped))(seeds)
            |> seedParallelWorkerTable(functions)(escaped)(rest)

// Stage 0's `ComputeParallelWorkerEffects`: the top-level functions that may run on a
// structured-parallelism worker, the least fixpoint from the callbacks passed to the parallel
// functions through their callees, and every escaped function where one makes a call the analysis
// cannot name. A function nested in a top-level one is counted as part of it.
let programParallelWorkerFunctions (table: List((Str, List(Str), Expr))) (body: Maybe(Expr)) =
    (let functions = programFunctionSet(table)(Ashes.Collection.Map.empty)
    in
        match scanProgramHandlerEffects(functions)(table)(entryHandlerEffectScan(functions)(body))([]) with
            | (entry, scans) ->
                let escaped = escapedFunctions(scans)(entry.references)
                in
                    let entrySeeds =
                        match body with
                            | Some(expression) ->
                                foldExpressionNodes(parallelWorkerSeedVisit(functions)(escaped))(Ashes.Collection.Map.empty)(expression)
                            | None -> Ashes.Collection.Map.empty
                    in
                        entrySeeds
                        |> seedParallelWorkerTable(functions)(escaped)(table)
                        |> liveFunctionsFixpoint(scans)(escaped))

let withProgramParameterOwnership (program: ProgramSyntax) (state: CoreLoweringState) =
    (let functionTable = topLevelFunctions(program)
    in
        state
        |> withStateProgramParameterOwnership(inferProgramParameterOwnership(functionTable))
        |> withStateMoveFunctionTable(functionTable)
        |> withStateMoveCallSites(collectAllCallSites(functionTable)(program.body))
        |> withStateLiveHandlerFunctions(programLiveHandlerFunctions(functionTable)(program.body))
        |> withStateParallelWorkerFunctions(programParallelWorkerFunctions(functionTable)(program.body))
        |> (given (seeded: CoreLoweringState) ->
            withStateUnderLiveHandlerPost(stateEntryRunsUnderLiveHandlerPost(seeded))(seeded))
        |> withStateMoveNullaryConstructors(programSoleNullaryConstructors(program.items))
        |> (given (seeded: CoreLoweringState) ->
            match buildReachRegistry(program) with
                | registry ->
                    seeded
                    |> withReachSummaries(computeReachSummaries(registry))
                    |> withStateReachRegistry(Some(registry))))

// Seeds the state with the result-reach summaries of the functions a bare expression binds.
let withExpressionReachSummaries (expression: Expr) (state: CoreLoweringState) =
    withReachSummaries(expressionReachSummaries(expression))(state)
