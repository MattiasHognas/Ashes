// Lowers the strict functional core into semantic IR before ownership placement.
//
// Invariants:
// - Callees are evaluated before arguments, and curried arguments are applied one at a time.
// - Conditions and match scrutinees are evaluated once; guarded arms retain source order.
// - Every function owns independent temp/local counters; closure env and argument locals are 0 and 1.
// - Captures follow first free-use order and occupy consecutive eight-byte environment words.
// - Recursive self/sibling references rebuild closures from the current shared environment.
// - Lifted functions retain generation order, with nested functions preceding their enclosing function.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Frontend.Syntax.LetBindingSyntax
import AshesCompiler.Frontend.Syntax.TopLevelItem
import AshesCompiler.Frontend.Syntax.ProgramSyntax
import AshesCompiler.Frontend.Syntax.TypeDecl
import AshesCompiler.Frontend.Syntax.TypeConstructor
import AshesCompiler.Frontend.Syntax.TypeParameter
import AshesCompiler.Frontend.Syntax.TypeExpr
import AshesCompiler.Frontend.Syntax.callArgumentsInline
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.CoreCapabilityLowering
import AshesCompiler.Semantics.CoreExternalLowering
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.ExternalTyping
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.OwnershipInference.classifyParameterOwnership
import AshesCompiler.Semantics.OwnershipInference.inferProgramParameterOwnership
import AshesCompiler.Semantics.OwnershipInference.lookupProgramParameterOwnership
import AshesCompiler.Semantics.OwnershipInference.topLevelFunctions
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.ResultReach.resultAlwaysReachesVariable
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.SourceContext
import AshesCompiler.Semantics.TaglessAdtLayout
import AshesCompiler.Semantics.TraitEvidenceRewriting
import AshesCompiler.Semantics.TraitEvidenceThreading
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.PerceusLifetimePlacement
import AshesCompiler.Semantics.Unification
export (
    type CoreLoweringError(..),
    type CoreLoweringResult(..),
    type CoreConstructorLayout(..),
    value lowerCoreExpression,
    value lowerCoreExpressionWithLayouts,
    value lowerCoreExpressionWithContext,
    value lowerCoreExpressionWithFullContext,
    value lowerCoreExpressionWithCompleteContext,
    value lowerCoreExpressionLocated,
    value lowerCoreRecursiveGroup,
    value pruneDeadCaptures,
    value lowerCoreProgram,
    value lowerCoreProgramWithSource,
    value lowerCoreProgramWithSourceAndContext,
    value lowerCoreProgramWithEnvironment,
)

type CoreLoweringError =
    | UnknownLoweringBinding(Str)
    | CoreCallRequiresFunction(SemanticType)
    | CoreCallTypeMismatch(UnificationError)
    | CoreOperatorTypeMismatch(Str, SemanticType, SemanticType)
    | CoreConstructorArityMismatch(Str, Int, Int)
    | CoreBuiltinArityMismatch(Str, Str, Int, Int)
    | UnsupportedCoreBuiltinLowering(Str)
    | CoreExternalDirectOnlyViolation(Str)
    | UnsupportedCoreExternalLowering(Str)
    | CoreUnhandledCapabilityOperation(Str, Str)
    | CoreAmbiguousCapabilitySatisfaction(Str)
    | UnknownCoreRecordField(Str, Str)
    | CoreRecordUpdateRequiresRecord(SemanticType)
    | UnsupportedCoreLoweringPattern(Str)
    | CoreRecursiveBindingRequiresFunction(Str)
    | UnsupportedCoreLoweringExpression(Str)
    | DuplicateTopLevelBinding(Str)
    | UnsupportedOperationArmResume(Str, Str)
    | ForwardTopLevelReference(Str)
    | UnresolvedTraitEvidenceForwarding(TraitEvidenceForwardingError)
    | UnsupportedTypeDeclaration(Str)
    | CoreMatchCoverageError(Str)
    | ReservedTypeName(Str)
    | PerformTargetNotCapabilityOperation(Str)
    | ResourceUseAfterClose(Str)
    | ResourceUseAfterMove(Str)
    | ResourceDoubleClose(Str)
    deriving {Eq, Show}

// How a resource binding stopped being its scope's responsibility: closed by an explicit close
// builtin, or moved into an aggregate, a consuming callee, or the arm result it escapes through. A
// binding with neither is live and closed at its scope exit.
type ResourceReleaseKind =
    | ResourceClosed
    | ResourceMoved
    deriving {Eq, Show}

type CoreLoweringResult =
    | program: Maybe(IrProgram)
    | semanticType: SemanticType
    | error: Maybe(CoreLoweringError)

type CoreConstructorLayout =
    | name: Str
    | tag: Int
    | scheme: TypeScheme
    | fieldNames: List(Str)
    | isZeroCost: Bool
    | tagless: Bool
    deriving {Eq, Show}

type CoreBindingLocation =
    | CoreLocal(Int)
    | CoreEnvironment(Int)
    | CoreSelf(Str, Int)

// `ownedRead` marks a `let` or pattern binding, whose reads borrow when the binding's resolved
// type is heap-represented (see `finishOwnedRead`); a parameter, capture, or recursive
// self-reference is not owned by its reader and loads plainly.
type CoreBinding =
    | name: Str
    | scheme: TypeScheme
    | location: CoreBindingLocation
    | ownedRead: Bool

// What the context asks of the next expression lowered, stage 0's `LoweredValueRequest`: the type
// it expects, whether it wants a fresh string placed on the reference-counted heap rather than in
// the arena (`RuntimeRepresentation.String`), and the owned `let` slot whose value the expression
// carries out as its result (`ConsumerCanOwn` for a tail-forwarded binding read).
type ConsumerRequest =
    | expectedType: Maybe(SemanticType)
    | runtimeString: Bool
    | transferSlot: Maybe(Int)

// A temp holding a reference-counted heap value: newly produced by its instruction (the consumer
// may take the reference) or already handed on.
type RuntimeTempState =
    | RuntimeNewlyProduced
    | RuntimeTransferred

let emptyConsumerRequest = ConsumerRequest(expectedType = None, runtimeString = false, transferSlot = None)

type CoreLoweringState =
    | reversedInstructions: List(IrInstruction)
    | functions: List(IrFunction)
    | bindings: List(CoreBinding)
    | constructorLayouts: List(CoreConstructorLayout)
    | builtinLayouts: List(CoreBuiltinLayout)
    | externalLayouts: List(CoreExternalFunctionLayout)
    | externalFunctions: List(ExternalFunctionAbi)
    | externalOpaqueTypes: List(Str)
    | capabilityLayouts: List(CoreCapabilityLayout)
    | staticProviders: List(CoreStaticProviderLayout)
    | capabilityGlobalCount: Int
    | nextTemp: Int
    | nextLocal: Int
    | nextLambdaId: Int
    | nextLabelId: Int
    | nextStringId: Int
    | stringLiterals: List(IrStringLiteral)
    | typeSupply: TypeVariableSupply
    | substitution: List((Int, SemanticType))
    | sourceContext: Maybe(SourceContext)
    | currentSpan: Maybe(TextSpan)
    | currentItem: Int
    | topLevelNames: List(Str)
    | pendingStackClosure: Bool
    | runtimeAdtRequested: Bool
    | pendingOperatorDefaults: List((Int, SemanticType))
    | sealedOperatorDefaults: List((Str, Int, SemanticType))
    | pendingSourceFunction: Maybe(SourceFunctionOrigin)
    | activeFunctionOrigin: Maybe(IrFunctionOrigin)
    | pendingClosureNormalizers: List((Str, IrFunctionOrigin, List(SemanticType), Maybe(IrSourceLocation)))
    | consumerRequest: ConsumerRequest
    | resourceStates: List((Int, ResourceReleaseKind))
    | letLambdas: List((Str, List(Str), Expr))
    | runtimeTemps: List((Int, RuntimeTempState))
    | runtimeOwners: List((Int, Bool))
    | bodyRuntimeManagedByLabel: List((Str, Bool))
    | letLambdaLabels: List((Str, Str))
    | runtimeNormalizedArgumentLabels: List(Str)
    | programParameterOwnership: List((Str, List((Str, ParameterOwnership))))
    | dropperLabels: DropperLabelCache

type LoweredCoreValue =
    | state: CoreLoweringState
    | temp: Int
    | semanticType: SemanticType
    | error: Maybe(CoreLoweringError)

type FreshTemp =
    | state: CoreLoweringState
    | temp: Int

type FreshLocal =
    | state: CoreLoweringState
    | local: Int

type FreshType =
    | state: CoreLoweringState
    | semanticType: SemanticType

type FreshLabel =
    | state: CoreLoweringState
    | label: Str

type FreshFunctionType =
    | state: CoreLoweringState
    | semanticType: SemanticType
    | parameterType: SemanticType
    | resultType: SemanticType

type LoweredCorePattern =
    | state: CoreLoweringState
    | error: Maybe(CoreLoweringError)

type CoreIfPlan =
    | state: CoreLoweringState
    | resultSlot: Int
    | elseLabel: Str
    | endLabel: Str
    | error: Maybe(CoreLoweringError)

type CoreIfThen =
    | state: CoreLoweringState
    | resultSlot: Int
    | endLabel: Str
    | thenType: SemanticType
    | error: Maybe(CoreLoweringError)

type CoreMatchPlan =
    | state: CoreLoweringState
    | valueTemp: Int
    | valueType: SemanticType
    | resultSlot: Int
    | endLabel: Str
    | noMatchLabel: Str
    | resultType: SemanticType
    | armRequest: ConsumerRequest
    | error: Maybe(CoreLoweringError)

// The cases of one match that share an outer constructor tag, in first-seen order. A group is a
// trivial single case only while it has exactly one case whose sub-patterns are all catch-alls,
// the one shape the switch can dispatch to without re-testing the tag it just proved.
type CoreTagGroup =
    | tag: Int
    | constructorName: Str
    | caseIndices: List(Int)
    | trivialSingleCase: Bool

type CoreCaseClass =
    | CaseConstructor(Str, Int, Str, Bool)
    | CaseDefault
    | CaseReject

type PreparedCoreRecursiveBinding =
    | name: Str
    | parameter: Str
    | body: Expr
    | label: Str
    | slot: Int
    | semanticType: SemanticType
    | parameterType: SemanticType
    | resultType: SemanticType

type PreparedCoreRecursiveGroup =
    | state: CoreLoweringState
    | members: List(PreparedCoreRecursiveBinding)
    | error: Maybe(CoreLoweringError)

type PreparedCoreRecursiveMemberResult =
    | state: CoreLoweringState
    | member: Maybe(PreparedCoreRecursiveBinding)
    | error: Maybe(CoreLoweringError)

type InstantiatedBinding =
    | state: CoreLoweringState
    | semanticType: SemanticType

type StringInterning =
    | state: CoreLoweringState
    | label: Str

type FunctionTypeResolution =
    | state: CoreLoweringState
    | argumentType: SemanticType
    | resultType: SemanticType
    | error: Maybe(CoreLoweringError)

type LoweredCoreValues =
    | state: CoreLoweringState
    | temps: List(Int)
    | semanticTypes: List(SemanticType)
    | error: Maybe(CoreLoweringError)

type LoweredCoreBinary =
    | state: CoreLoweringState
    | leftTemp: Int
    | leftType: SemanticType
    | rightTemp: Int
    | rightType: SemanticType
    | error: Maybe(CoreLoweringError)

type CoreBinaryOperator =
    | CoreAddOperator
    | CoreSubtractOperator
    | CoreMultiplyOperator
    | CoreDivideOperator
    | CoreModuloOperator
    | CoreBitwiseAndOperator
    | CoreBitwiseOrOperator
    | CoreBitwiseXorOperator
    | CoreShiftLeftOperator
    | CoreShiftRightOperator
    | CoreGreaterOperator
    | CoreGreaterOrEqualOperator
    | CoreLessOperator
    | CoreLessOrEqualOperator
    | CoreEqualOperator
    | CoreNotEqualOperator

type ReservedCoreTemps =
    | state: CoreLoweringState
    | first: Int

type CoreConstructorShape =
    | state: CoreLoweringState
    | layout: CoreConstructorLayout
    | parameterTypes: List(SemanticType)
    | resultType: SemanticType
    | constructorRuntimeManaged: Bool

type CoreBuiltinShape =
    | state: CoreLoweringState
    | layout: CoreBuiltinLayout
    | parameterTypes: List(SemanticType)
    | resultType: SemanticType

type CoreCallSpine =
    | root: Expr
    | arguments: List(Expr)

type CoreRecordArguments =
    | expressions: List(Expr)
    | error: Maybe(CoreLoweringError)

type FreshCoreTypes =
    | state: CoreLoweringState
    | semanticTypes: List(SemanticType)

type CorePatternField =
    | index: Int
    | semanticType: SemanticType

let emptyScheme semanticType =
    TypeScheme(
        quantified = [],
        body = semanticType,
        constraints = []
    )

let initialStateWithCompleteContext constructorLayouts builtinLayouts externalLayouts externalFunctions externalOpaqueTypes capabilityLayouts staticProviders capabilityGlobalCount unit =
    CoreLoweringState(
        reversedInstructions = [],
        functions = [],
        bindings = [],
        constructorLayouts = constructorLayouts,
        builtinLayouts = builtinLayouts,
        externalLayouts = externalLayouts,
        externalFunctions = externalFunctions,
        externalOpaqueTypes = externalOpaqueTypes,
        capabilityLayouts = capabilityLayouts,
        staticProviders = staticProviders,
        capabilityGlobalCount = capabilityGlobalCount,
        nextTemp = 0,
        nextLocal = 0,
        nextLambdaId = 0,
        nextLabelId = 0,
        nextStringId = 0,
        stringLiterals = [],
        // Starts past `standardBuiltinLayouts`' own reserved ids (see
        // `reservedBuiltinTypeVariableCount`'s own comment) rather than `0` — even a caller
        // passing empty `constructorLayouts`/`builtinLayouts` gets this, harmlessly: it just means
        // this compilation's fresh-variable numbering never mints those specific low ids, not that
        // anything is missing.
        typeSupply = TypeVariableSupply(nextId = reservedBuiltinTypeVariableCount),
        substitution = [],
        sourceContext = None,
        currentSpan = None,
        currentItem = 0,
        topLevelNames = [],
        pendingStackClosure = false,
        runtimeAdtRequested = false,
        pendingOperatorDefaults = [],
        sealedOperatorDefaults = [],
        pendingSourceFunction = None,
        activeFunctionOrigin = None,
        pendingClosureNormalizers = [],
        consumerRequest = emptyConsumerRequest,
        resourceStates = [],
        letLambdas = [],
        runtimeTemps = [],
        runtimeOwners = [],
        bodyRuntimeManagedByLabel = [],
        letLambdaLabels = [],
        runtimeNormalizedArgumentLabels = [],
        programParameterOwnership = [],
        dropperLabels = emptyDropperLabelCache
    )

let initialStateWithFullContext constructorLayouts builtinLayouts externalLayouts externalFunctions externalOpaqueTypes unit = initialStateWithCompleteContext(constructorLayouts)(builtinLayouts)(externalLayouts)(externalFunctions)(externalOpaqueTypes)([])([])(0)(unit)

let initialStateWithContext constructorLayouts builtinLayouts unit = initialStateWithFullContext(constructorLayouts)(builtinLayouts)([])([])([])(unit)

let initialStateWithLayouts layouts unit = initialStateWithContext(layouts)([])(unit)

// "`Unit` is always available; no import is required" (language.md) — a builtin's `Unit` result
// (`finishBuiltinUnit`) allocates through this constructor layout the same way a user's own
// zero-field ADT would, so it has to be intrinsic like `standardBuiltinLayouts`, not something
// every caller re-supplies. `tag = 0`, no fields, matching `AllocAdt Tag=0 FieldCount=0` in
// stage-0's own `--emit-ir` dump for any `Unit`-returning call.
//
// `Maybe` and `Result` get the same intrinsic treatment for the same reason: language.md states
// both "`Maybe` is always available; no import is required" and "`Result` is always available; no
// import is required". Tags/field counts match stage-0's own `--emit-ir` dump exactly (probed via
// `let x = Some(42)`/`None`/`Ok(1)`/`Error("e")`, each `x` on its own trailing line): `None` is
// `AllocAdt Tag=0 FieldCount=0`, `Some` is `AllocAdt Tag=1 FieldCount=1` (+ one `SetAdtField`), `Ok`
// is `AllocAdt Tag=0 FieldCount=1`, `Error` is `AllocAdt Tag=1 FieldCount=1` — `Maybe` and `Result`
// each number their own tags from 0, not a single tag space shared across every ADT. `fieldNames`
// stays `[]` for all four the same way it does for `Unit`: these are plain positional constructors,
// never constructed with record syntax, and `fieldNames` only matters for the record-construction
// path (see `findRecordLayout`). Quantified ids `1`-`3` are fresh past `print`'s own `0` (see
// `reservedBuiltinTypeVariableCount`'s comment for why an unused id would be unsafe); `Some` and
// `None` both reuse id `1` for their own `a`, which is fine because `instantiate` mints an
// independent substitution per call — only a *live* supply value colliding with a *reserved* id is
// the actual hazard, not two static schemes sharing one.
let maybeElementType = SemVariable(1)

let maybeType = SemNamed(0)("Maybe")([maybeElementType])

let resultErrorType = SemVariable(2)

let resultValueType = SemVariable(3)

let resultType = SemNamed(0)("Result")([resultErrorType, resultValueType])

let standardConstructorLayouts =
    [
        CoreConstructorLayout(
            name = "Unit",
            tag = 0,
            scheme = TypeScheme(quantified = [], body = SemNamed(0)("Unit")([]), constraints = []),
            fieldNames = [],
            isZeroCost = false,
            tagless = false
        ),
        CoreConstructorLayout(
            name = "None",
            tag = 0,
            scheme = TypeScheme(quantified = [(1, "a")], body = maybeType, constraints = []),
            fieldNames = [],
            isZeroCost = false,
            tagless = false
        ),
        CoreConstructorLayout(
            name = "Some",
            tag = 1,
            scheme = TypeScheme(quantified = [(1, "a")], body = SemFunction(maybeElementType)(maybeType)(None), constraints = []),
            fieldNames = [],
            isZeroCost = false,
            tagless = false
        ),
        CoreConstructorLayout(
            name = "Ok",
            tag = 0,
            scheme = TypeScheme(quantified = [(2, "e"), (3, "a")], body = SemFunction(resultValueType)(resultType)(None), constraints = []),
            fieldNames = [],
            isZeroCost = false,
            tagless = false
        ),
        CoreConstructorLayout(
            name = "Error",
            tag = 1,
            scheme = TypeScheme(quantified = [(2, "e"), (3, "a")], body = SemFunction(resultErrorType)(resultType)(None), constraints = []),
            fieldNames = [],
            isZeroCost = false,
            tagless = false
        )
    ]

// The real language never requires an `import` for a qualified `Ashes.*` builtin call ("qualified
// access (no import required)", language.md) — availability has to be intrinsic to the lowering
// pipeline, not something every caller re-supplies through `initialStateWithContext`. `initialState`
// is the "just give me sensible defaults" entry point (`lowerCoreProgram`/`lowerCoreProgramWithSource`
// both go through it); `initialStateWithContext` and friends remain fully caller-controlled for
// whoever genuinely needs a different (or additional) set.
let initialState unit = initialStateWithContext(standardConstructorLayouts)(standardBuiltinLayouts)(unit)

let withNextTemp nextTemp (state: CoreLoweringState) = state with nextTemp = nextTemp

let withNextLocal nextLocal (state: CoreLoweringState) = state with nextLocal = nextLocal

let withTypeSupply typeSupply (state: CoreLoweringState) = state with typeSupply = typeSupply

let withSubstitution substitution (state: CoreLoweringState) = state with substitution = substitution

let withNextLambdaId nextLambdaId (state: CoreLoweringState) = state with nextLambdaId = nextLambdaId

let withNextLabelId nextLabelId (state: CoreLoweringState) = state with nextLabelId = nextLabelId

let freshTemp state =
    match state with
        | CoreLoweringState { nextTemp = nextTemp } ->
            FreshTemp(
                state = withNextTemp(nextTemp + 1)(state),
                temp = nextTemp
            )

let freshLocal state =
    match state with
        | CoreLoweringState { nextLocal = nextLocal } ->
            FreshLocal(
                state = withNextLocal(nextLocal + 1)(state),
                local = nextLocal
            )

let freshType state =
    match state with
        | CoreLoweringState { typeSupply = supply } ->
            match freshTypeVariable(supply) with
                | (semanticType, nextSupply) ->
                    FreshType(
                        state = withTypeSupply(nextSupply)(state),
                        semanticType = semanticType
                    )

let freshLabel prefix state =
    match state with
        | CoreLoweringState { nextLabelId = nextLabelId } ->
            FreshLabel(
                state = withNextLabelId(nextLabelId + 1)(state),
                label = prefix + "_" + Ashes.Text.fromInt(nextLabelId)
            )

let freshFunctionType state =
    match freshType(state) with
        | FreshType { state = parameterState, semanticType = parameterType } ->
            match freshType(parameterState) with
                | FreshType { state = resultState, semanticType = resultType } ->
                    FreshFunctionType(
                        state = resultState,
                        semanticType = SemFunction(parameterType)(resultType)(None),
                        parameterType = parameterType,
                        resultType = resultType
                    )

// Every emitted instruction carries the innermost enclosing source span resolved through the
// installed source context (runtime machinery stays unlocated); without a context, no location.
let emit kind state =
    match state with
        | CoreLoweringState { reversedInstructions = instructions, sourceContext = context, currentSpan = span, currentItem = item } ->
            let wrapped = tagItemInstruction(kind)(span)(item)(context)
            in state with reversedInstructions = wrapped :: instructions

let success temp semanticType state =
    LoweredCoreValue(
        state = state,
        temp = temp,
        semanticType = semanticType,
        error = None
    )

// The innermost enclosing span resolved the way emitted instructions resolve theirs.
let currentLocation (state: CoreLoweringState) =
    match (state.currentSpan, state.sourceContext) with
        | (Some(span), Some(context)) -> resolveItemSpanLocation(context)(state.currentItem)(span)
        | _ -> None

let spanStart (span: Maybe(TextSpan)) =
    match span with
        | Some(TextSpan { start = start }) -> start
        | None -> 0

let lambdaSiteDiscriminator (parameter: Str) (span: Maybe(TextSpan)) =
    match span with
        | Some(TextSpan { start = start, end = end }) -> "lambda:" + Ashes.Text.fromInt(start) + ":" + Ashes.Text.fromInt(end - start) + ":" + parameter
        | None -> "lambda:0:0:" + parameter

// A let-bound name as the source function its lambda value is lifted from. The qualified name
// stage 0 maps from module-qualified binding names is not tracked here.
let sourceFunctionOriginFor (name: Str) (state: CoreLoweringState) =
    SourceFunctionOrigin(
        functionSourceName = name,
        functionQualifiedName = None,
        declarationLocation = currentLocation(state),
        declarationOffset = spanStart(state.currentSpan)
    )

let recursive letValueIsLambda (value: Expr) =
    match value with
        | ExprAt(_span, inner) -> letValueIsLambda(inner)
        | ExprLambda(_parameter, _body, _annotation) -> true
        | _ -> false

// Arms a `let` whose value is a lambda so the lambda lowered next is lifted as that name's
// `SourceFunction`; any other value leaves the lambda origins alone. A lambda under `let ... in`
// wrappers inside the value is not armed and lifts as a closure helper.
// The parameter chain and innermost body of a curried lambda value.
let recursive lambdaParameterChain (value: Expr) (parameters: List(Str)) =
    match value with
        | ExprAt(_span, inner) -> lambdaParameterChain(inner)(parameters)
        | ExprLambda(parameter, body, _annotation) -> lambdaParameterChain(body)(parameter :: parameters)
        | body -> (reverse(parameters), body)

// A let-bound lambda is remembered by name so a later call through it can ask which of its
// parameters it only borrows (stage 0's per-function ownership summary).
let recordLetLambda (name: Str) (value: Expr) (state: CoreLoweringState) =
    match lambdaParameterChain(value)([]) with
        | (parameters, body) -> state with letLambdas = (name, parameters, body) :: state.letLambdas

let armSourceFunction (name: Str) (value: Expr) (stackClosure: Bool) (state: CoreLoweringState) =
    if letValueIsLambda(value)
    then
        recordLetLambda(name)(value)((state with pendingSourceFunction = Some(sourceFunctionOriginFor(name)(state)), pendingStackClosure = stackClosure))
    else state

let sourceFunctionOrigin (label: Str) (source: SourceFunctionOrigin) (state: CoreLoweringState) =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = SourceFunctionOriginKind,
        sourceOrigin = Some(source),
        parentGeneratedLabel = None,
        compilerOwner = None,
        stableDiscriminator = None,
        generationLocation = currentLocation(state)
    )

let closureHelperOrigin (label: Str) (parameter: Str) (parent: Maybe(IrFunctionOrigin)) (state: CoreLoweringState) =
    match parent with
        | Some(IrFunctionOrigin { generatedLabel = parentLabel, sourceOrigin = source }) ->
            IrFunctionOrigin(
                generatedLabel = label,
                originKind = ClosureHelperOrigin,
                sourceOrigin = source,
                parentGeneratedLabel = Some(parentLabel),
                compilerOwner = None,
                stableDiscriminator = state.currentSpan
                |> lambdaSiteDiscriminator(parameter)
                |> Some,
                generationLocation = currentLocation(state)
            )
        | None ->
            IrFunctionOrigin(
                generatedLabel = label,
                originKind = ClosureHelperOrigin,
                sourceOrigin = None,
                parentGeneratedLabel = None,
                compilerOwner = Some(CompilerFunctionOwner(ownerKind = ProgramFunctionOwner, ownerName = "anonymous source function")),
                stableDiscriminator = state.currentSpan
                |> lambdaSiteDiscriminator(parameter)
                |> Some,
                generationLocation = currentLocation(state)
            )

// The origin of the lambda about to be lifted as `label`: the armed let name's source function,
// else a closure helper of the enclosing source function, else an anonymous closure helper.
let lambdaOriginFor (label: Str) (parameter: Str) (state: CoreLoweringState) =
    match state.pendingSourceFunction with
        | Some(source) -> sourceFunctionOrigin(label)(source)(state)
        | None -> closureHelperOrigin(label)(parameter)(state.activeFunctionOrigin)(state)

let enterFunctionOrigin (origin: IrFunctionOrigin) (state: CoreLoweringState) = state with pendingSourceFunction = None, pendingStackClosure = false, activeFunctionOrigin = Some(origin)

// One scoped-arena bracket's two watermark slots, carried from its `SaveArenaState` to the
// matching restore. Every bracket stage 0 emits — around a top-level `let`, a nested `let`, and
// each `match` arm — is this same triple: save two slots on entry, then restore/reclaim against a
// third `preRestoreEnd` slot allocated at the closing point.
type ArenaBracket =
    | bracketState: CoreLoweringState
    | bracketCursorSlot: Int
    | bracketEndSlot: Int

let openArenaBracket state =
    match freshLocal(state) with
        | FreshLocal { state = cursorAllocated, local = cursorSlot } ->
            match freshLocal(cursorAllocated) with
                | FreshLocal { state = endAllocated, local = endSlot } ->
                    ArenaBracket(
                        bracketState = emit(SaveArenaState(cursorSlot)(endSlot)(false))(endAllocated),
                        bracketCursorSlot = cursorSlot,
                        bracketEndSlot = endSlot
                    )

// A bracket can be closed more than once (a `match` arm closes on both its success path and its
// cleanup path), and stage 0 allocates a FRESH `preRestoreEnd` slot at each closing point rather
// than reusing one — so this allocates per call, never per bracket.
let closeArenaBracket cursorSlot endSlot state =
    match freshLocal(state) with
        | FreshLocal { state = preRestoreAllocated, local = preRestoreSlot } ->
            preRestoreAllocated
            |> emit(RestoreArenaState(cursorSlot)(endSlot)(preRestoreSlot)(false))
            |> emit(ReclaimArenaChunks(endSlot)(preRestoreSlot)(false))

let failure state error =
    LoweredCoreValue(
        state = state,
        temp = -1,
        semanticType = SemNever,
        error = Some(error)
    )

let bindingScheme binding =
    match binding with
        | CoreBinding { scheme = scheme } -> scheme

let recursive bindingSchemes bindings =
    match bindings with
        | [] -> []
        | binding :: rest -> bindingScheme(binding) :: bindingSchemes(rest)

let recursive lookupBinding name bindings =
    match bindings with
        | [] -> None
        | (CoreBinding { name = candidate } as binding) :: rest ->
            if name == candidate
            then Some(binding)
            else lookupBinding(name)(rest)

let instantiateBinding binding state =
    match (binding, state) with
        | (CoreBinding { scheme = scheme }, CoreLoweringState { typeSupply = supply }) ->
            match instantiate(scheme)(supply) with
                | InstantiationResult { semanticType = semanticType, supply = nextSupply } ->
                    InstantiatedBinding(
                        state = withTypeSupply(nextSupply)(state),
                        semanticType = semanticType
                    )

let resolveType state semanticType =
    match state with
        | CoreLoweringState { substitution = substitution } -> applySubstitution(substitution)(semanticType)

let bindType left right state =
    match state with
        | CoreLoweringState { substitution = existing } ->
            match right
            |> resolveType(state)
            |> unify(resolveType(state)(left)) with
                | UnificationResult { substitution = added, error = None } ->
                    (withSubstitution(append(added)(existing))(state), None)
                | UnificationResult { error = Some(error) } -> (state, Some(CoreCallTypeMismatch(error)))

// The type the context expects of the next expression lowered, threaded through the state:
// `lowerCore` consumes it. A let, recursive binding, lambda, if, match, handle, call, list
// literal, or cons forwards it to the parts stage 0 forwards it to; every other expression is
// lowered without it and unified with it afterwards. The result state never carries one.
let consumerRequestOf (state: CoreLoweringState) = state.consumerRequest

let withConsumerRequest (request: ConsumerRequest) (state: CoreLoweringState) = state with consumerRequest = request

let clearConsumerRequest (state: CoreLoweringState) = withConsumerRequest(emptyConsumerRequest)(state)

let expectedTypeOf (state: CoreLoweringState) =
    match consumerRequestOf(state) with
        | ConsumerRequest { expectedType = expected } -> expected

// A request carrying only an expected type: what a call argument, a list element, or a cons tail
// is asked for.
let withOnlyExpectedType expected (state: CoreLoweringState) = withConsumerRequest((emptyConsumerRequest with expectedType = expected))(state)

// The request a branch or arm inherits: the context's, minus the binding transfer only a
// straight `let` chain forwards.
let branchRequest (request: ConsumerRequest) = request with transferSlot = None

let withLoweredConsumerRequest (request: ConsumerRequest) (lowered: LoweredCoreValue) = lowered with state = withConsumerRequest(request)(lowered.state)

let runtimeStringRequested (state: CoreLoweringState) =
    match consumerRequestOf(state) with
        | ConsumerRequest { runtimeString = requested } -> requested

// The reference-counted heap temps of the current function.
let recursive lookupRuntimeTemp (temp: Int) (temps: List((Int, RuntimeTempState))) =
    match temps with
        | [] -> None
        | (candidate, runtimeState) :: rest ->
            if candidate == temp
            then Some(runtimeState)
            else lookupRuntimeTemp(temp)(rest)

let runtimeTempStateOf (temp: Int) (state: CoreLoweringState) = lookupRuntimeTemp(temp)(state.runtimeTemps)

let isRuntimeTemp (temp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(temp)(state) with
        | Some(_runtimeState) -> true
        | None -> false

let markRuntimeTemp (temp: Int) (runtimeState: RuntimeTempState) (state: CoreLoweringState) = state with runtimeTemps = (temp, runtimeState) :: state.runtimeTemps

let markLoweredRuntimeTemp (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state, temp = temp } -> lowered with state = markRuntimeTemp(temp)(RuntimeNewlyProduced)(state)

// Whether a lifted function's body result is a reference-counted heap value, recorded when the
// body is finished and read back by the closure carrying the function and by known calls to it.
let recursive lookupBodyRuntimeManaged (label: Str) (entries: List((Str, Bool))) =
    match entries with
        | [] -> false
        | (candidate, runtimeManaged) :: rest ->
            if candidate == label
            then runtimeManaged
            else lookupBodyRuntimeManaged(label)(rest)

let bodyReturnsRuntimeManaged (label: Str) (state: CoreLoweringState) = lookupBodyRuntimeManaged(label)(state.bodyRuntimeManagedByLabel)

let recordBodyRuntimeManaged (label: Str) (runtimeManaged: Bool) (state: CoreLoweringState) = state with bodyRuntimeManagedByLabel = (label, runtimeManaged) :: state.bodyRuntimeManagedByLabel

// Whether a lifted function normalizes its argument into an owned value at entry, so its closure
// advertises that it accepts a runtime-managed argument and a caller hands over a retained
// reference.
let recursive containsLabel (label: Str) (labels: List(Str)) =
    match labels with
        | [] -> false
        | candidate :: rest -> candidate == label || containsLabel(label)(rest)

let acceptsRuntimeManagedArgument (label: Str) (state: CoreLoweringState) = containsLabel(label)(state.runtimeNormalizedArgumentLabels)

let recordRuntimeNormalizedArgument (label: Str) (state: CoreLoweringState) = state with runtimeNormalizedArgumentLabels = label :: state.runtimeNormalizedArgumentLabels

// Stage 0's `ReleaseConsumedOwnedOperand`: a consumer that keeps nothing of a newly produced
// reference-counted string releases it right after the use.
let releaseConsumedOperand (temp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(temp)(state) with
        | Some(RuntimeNewlyProduced) ->
            state
            |> emit(RcDrop(temp)("String")(-1)(true)(false)(None))
            |> markRuntimeTemp(temp)(RuntimeTransferred)
        | _ -> state

let unifyExpectedResult expected lowered =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            match bindType(expected)(semanticType)(state) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (typedState, None) ->
                    success(temp)(resolveType(typedState)(semanticType))(typedState)

let recursive expectedTypeForwards expression =
    match expression with
        | ExprAt(_span, inner) -> expectedTypeForwards(inner)
        | ExprLet(_, _, _, _, _, _) -> true
        | ExprLetRecursive(_, _, _, _, _, _) -> true
        | ExprLambda(_, _, _) -> true
        | ExprIf(_, _, _) -> true
        | ExprMatch(_, _, _) -> true
        | ExprHandle(_, _) -> true
        | ExprCall(_, _, _, _) -> true
        | ExprList(_, _) -> true
        | ExprCons(_, _) -> true
        | _ -> false

// The runtime-string request reaches every kind the expected type reaches, and `+`, whose
// string concatenation is itself the producer that honors it.
let recursive runtimeRequestForwards expression =
    match expression with
        | ExprAt(_span, inner) -> runtimeRequestForwards(inner)
        | ExprAdd(_, _) -> true
        | other -> expectedTypeForwards(other)

// A binding transfer only reaches the straight `let` chain down to the binding's own read.
let recursive transferForwards expression =
    match expression with
        | ExprAt(_span, inner) -> transferForwards(inner)
        | ExprLet(_, _, _, _, _, _) -> true
        | ExprLetRecursive(_, _, _, _, _, _) -> true
        | ExprVar(_) -> true
        | _ -> false

let dispatchRequest (expression: Expr) (request: ConsumerRequest) =
    ConsumerRequest(
        expectedType = if expectedTypeForwards(expression)
        then request.expectedType
        else None,
        runtimeString = runtimeRequestForwards(expression) && request.runtimeString,
        transferSlot = if transferForwards(expression)
        then request.transferSlot
        else None
    )

let unifyUnforwardedExpectedType (expression: Expr) (expected: Maybe(SemanticType)) (lowered: LoweredCoreValue) =
    match expected with
        | Some(expectedType) ->
            if expectedTypeForwards(expression)
            then lowered
            else unifyExpectedResult(expectedType)(lowered)
        | None -> lowered

let recursive findConstructorLayout (name: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> None
        | (CoreConstructorLayout { name = candidate } as layout) :: rest ->
            if name == candidate
            then Some(layout)
            else findConstructorLayout(name)(rest)

let constructorLayout name state =
    match state with
        | CoreLoweringState { constructorLayouts = layouts } -> findConstructorLayout(name)(layouts)

let recursive findBuiltinLayout moduleName memberName layouts =
    match layouts with
        | [] -> None
        | (CoreBuiltinLayout { moduleName = candidateModule, memberName = candidateMember } as layout) :: rest ->
            if moduleName == candidateModule
            then
                if memberName == candidateMember
                then Some(layout)
                else findBuiltinLayout(moduleName)(memberName)(rest)
            else findBuiltinLayout(moduleName)(memberName)(rest)

let builtinLayout moduleName memberName state =
    match state with
        | CoreLoweringState { builtinLayouts = layouts } -> findBuiltinLayout(moduleName)(memberName)(layouts)

let recursive splitConstructorType semanticType reversed =
    match semanticType with
        | SemFunction(parameterType, resultType, _row) -> splitConstructorType(resultType)(parameterType :: reversed)
        | resultType -> (reverse(reversed), resultType)

// A constructor allocates in the arena unless its consumer requested a runtime-managed (RC) cell
// through `runtimeAdtRequested`. The request is consumed here, before the constructor's arguments
// are lowered, so a nested constructor argument allocates in the arena as usual.
let instantiateConstructor layout state =
    match (layout, state) with
        | (CoreConstructorLayout { scheme = scheme }, CoreLoweringState { typeSupply = supply, runtimeAdtRequested = requested }) ->
            match instantiate(scheme)(supply) with
                | InstantiationResult { semanticType = semanticType, supply = nextSupply } ->
                    match splitConstructorType(semanticType)([]) with
                        | (parameterTypes, resultType) ->
                            CoreConstructorShape(
                                state = withTypeSupply(nextSupply)((state with runtimeAdtRequested = false)),
                                layout = layout,
                                parameterTypes = parameterTypes,
                                resultType = resultType,
                                constructorRuntimeManaged = requested
                            )

let instantiateBuiltin layout state =
    match (layout, state) with
        | (CoreBuiltinLayout { scheme = scheme }, CoreLoweringState { typeSupply = supply }) ->
            match instantiate(scheme)(supply) with
                | InstantiationResult { semanticType = semanticType, supply = nextSupply } ->
                    match splitConstructorType(semanticType)([]) with
                        | (parameterTypes, resultType) ->
                            CoreBuiltinShape(
                                state = withTypeSupply(nextSupply)(state),
                                layout = layout,
                                parameterTypes = parameterTypes,
                                resultType = resultType
                            )

let recursive coreListLength values =
    match values with
        | [] -> 0
        | _ :: rest -> 1 + coreListLength(rest)

let constructorResultName layout =
    match layout with
        | CoreConstructorLayout { scheme = TypeScheme { body = body } } ->
            match splitConstructorType(body)([]) with
                | (_parameters, SemNamed(_symbolId, name, _arguments)) -> Some(name)
                | _ -> None

let constructorArity layout =
    match layout with
        | CoreConstructorLayout { scheme = TypeScheme { body = body } } ->
            match splitConstructorType(body)([]) with
                | (parameterTypes, _resultType) -> coreListLength(parameterTypes)

let builtinArity layout =
    match layout with
        | CoreBuiltinLayout { scheme = TypeScheme { body = body } } ->
            match splitConstructorType(body)([]) with
                | (parameterTypes, _resultType) -> coreListLength(parameterTypes)

let recursive findRecordLayout (typeName: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> None
        | (CoreConstructorLayout { fieldNames = _field :: _rest } as layout) :: tail ->
            match constructorResultName(layout) with
                | Some(candidate) ->
                    if candidate == typeName
                    then Some(layout)
                    else findRecordLayout(typeName)(tail)
                | None -> findRecordLayout(typeName)(tail)
        | _layout :: tail -> findRecordLayout(typeName)(tail)

let recordLayout typeName state =
    match state with
        | CoreLoweringState { constructorLayouts = layouts } -> findRecordLayout(typeName)(layouts)

let failedCoreValues state error =
    LoweredCoreValues(
        state = state,
        temps = [],
        semanticTypes = [],
        error = Some(error)
    )

let finishCoreValues state reversedTemps reversedTypes =
    LoweredCoreValues(
        state = state,
        temps = reverse(reversedTemps),
        semanticTypes = reverse(reversedTypes),
        error = None
    )

let recursive lowerCoreValuesInto expressions lower state reversedTemps reversedTypes =
    match expressions with
        | [] -> finishCoreValues(state)(reversedTemps)(reversedTypes)
        | expression :: rest ->
            match lower(expression)(state) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failedCoreValues(failedState)(error)
                | LoweredCoreValue { state = nextState, temp = temp, semanticType = semanticType, error = None } ->
                    lowerCoreValuesInto(
                        rest,
                        lower,
                        nextState,
                        temp :: reversedTemps,
                        semanticType :: reversedTypes
                    )

let lowerCoreValues expressions lower state = lowerCoreValuesInto(expressions)(lower)(state)([])([])

let recursive bindCoreValueTypes expected actual state =
    match (expected, actual) with
        | ([], []) -> (state, None)
        | (expectedHead :: expectedTail, actualHead :: actualTail) ->
            match bindType(expectedHead)(actualHead)(state) with
                | (failedState, Some(error)) -> (failedState, Some(error))
                | (typedState, None) -> bindCoreValueTypes(expectedTail)(actualTail)(typedState)
        | _ ->
            (state, Some(actual
            |> coreListLength
            |> TypeArityMismatch(
                coreListLength(expected)
            )
            |> CoreCallTypeMismatch))

let lowerConstant kind semanticType state =
    match freshTemp(state) with
        | FreshTemp { state = nextState, temp = temp } ->
            nextState
            |> emit(kind(temp))
            |> success(temp)(semanticType)

let recursive findStringLiteral value literals =
    match literals with
        | [] -> None
        | IrStringLiteral { label = label, value = candidate } :: rest ->
            if value == candidate
            then Some(label)
            else findStringLiteral(value)(rest)

let internString value state =
    match state with
        | CoreLoweringState { stringLiterals = literals, nextStringId = nextId } ->
            match findStringLiteral(value)(literals) with
                | Some(label) -> StringInterning(state = state, label = label)
                | None ->
                    let label = "str_" + Ashes.Text.fromInt(nextId)
                    in
                        StringInterning(
                            state = state
                            |> (given (current: CoreLoweringState) -> current with nextStringId = nextId + 1)
                            |> (given (current: CoreLoweringState) ->
                                current with stringLiterals = append(literals)([
                                    IrStringLiteral(label = label, value = value)
                                ])),
                            label = label
                        )

let lowerString value state =
    match internString(value)(state) with
        | StringInterning { state = internedState, label = label } ->
            lowerConstant(given (target) -> LoadConstStr(target)(label))(SemString)(internedState)

// The payload type a zero-cost single-constructor type is represented as, when `name` names one.
let recursive zeroCostPayloadType (name: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> None
        | CoreConstructorLayout { isZeroCost = true, scheme = TypeScheme { body = SemFunction(payload, SemNamed(_symbolId, candidate, _arguments), _row) } } :: rest ->
            if candidate == name
            then Some(payload)
            else zeroCostPayloadType(name)(rest)
        | _ :: rest -> zeroCostPayloadType(name)(rest)

// The type name an owned binding carries, stage 0's `GetOwnedTypeName`: a heap-represented value
// (string, bytes, big integer, list, tuple, closure, or a declared type, seen through a zero-cost
// wrapper to its payload) is owned; copy types and unresolved type variables are not. A zero-cost
// wrapper whose payload is a type variable erases to that variable and so counts as not owned.
let recursive ownedTypeNameOf (semanticType: SemanticType) (layouts: List(CoreConstructorLayout)) =
    match semanticType with
        | SemString -> Some("String")
        | SemBytes -> Some("Bytes")
        | SemBigInt -> Some("BigInt")
        | SemList(_element) -> Some("List")
        | SemTuple(_elements) -> Some("Tuple")
        | SemFunction(_parameter, _result, _row) -> Some("Function")
        | SemNamed(_symbolId, name, _arguments) ->
            match zeroCostPayloadType(name)(layouts) with
                | Some(payload) -> ownedTypeNameOf(payload)(layouts)
                | None -> Some(name)
        | _ -> None

// The compiler-provided resource types (§16 of the language reference) the lowering tracks.
let isResourceTypeName (name: Str) = name == "FileHandle" || name == "Process"

// The destructor of a declared external resource (`external type T resource destructor f`): the
// external function naming `T` as the resource it destroys.
let recursive findResourceDestructor (typeName: Str) (functions: List(ExternalFunctionAbi)) =
    match functions with
        | [] -> None
        | (ExternalFunctionAbi { destructorForResource = Some(resource) } as abi) :: rest ->
            if resource == typeName
            then Some(abi)
            else findResourceDestructor(typeName)(rest)
        | _ :: rest -> findResourceDestructor(typeName)(rest)

let declaredResourceDestructor (typeName: Str) (state: CoreLoweringState) = findResourceDestructor(typeName)(state.externalFunctions)

let isDeclaredResourceName (typeName: Str) (state: CoreLoweringState) =
    match declaredResourceDestructor(typeName)(state) with
        | Some(_abi) -> true
        | None -> false

// A resource type name: a compiler-provided handle or a declared external resource.
let isResourceTypeNameIn (typeName: Str) (state: CoreLoweringState) = isResourceTypeName(typeName) || isDeclaredResourceName(typeName)(state)

// The resource type a resolved type is, through zero-cost wrappers: a compiler-provided handle
// by its owned type name, a declared external resource by its opaque name.
let resourceTypeNameOf (semanticType: SemanticType) (state: CoreLoweringState) =
    match semanticType with
        | SemOpaque(name) ->
            if isDeclaredResourceName(name)(state)
            then Some(name)
            else None
        | other ->
            match ownedTypeNameOf(other)(state.constructorLayouts) with
                | Some(typeName) ->
                    if isResourceTypeName(typeName)
                    then Some(typeName)
                    else None
                | None -> None

let recursive unspanArgument (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> unspanArgument(inner)
        | other -> other

// The resource a variable names: the slot and resource type of an owned local binding whose
// resolved type is a resource.
let resourceBindingOf (name: Str) (state: CoreLoweringState) =
    match lookupBinding(name)(state.bindings) with
        | Some(CoreBinding { location = CoreLocal(slot), ownedRead = true, scheme = TypeScheme { body = bindingType } }) ->
            match resourceTypeNameOf(resolveType(state)(bindingType))(state) with
                | Some(typeName) -> Some((slot, typeName))
                | None -> None
        | _ -> None

let recursive lookupResourceState (slot: Int) (states: List((Int, ResourceReleaseKind))) =
    match states with
        | [] -> None
        | (candidate, kind) :: rest ->
            if candidate == slot
            then Some(kind)
            else lookupResourceState(slot)(rest)

let resourceStateOf (slot: Int) (state: CoreLoweringState) = lookupResourceState(slot)(state.resourceStates)

let markResourceReleased (slot: Int) (kind: ResourceReleaseKind) (state: CoreLoweringState) = state with resourceStates = (slot, kind) :: state.resourceStates

// The state of the resource a variable argument names, when it names one: `Some(None)` for a live
// resource, `Some(Some(kind))` for a released one, `None` for anything else.
let resourceArgumentState (argument: Expr) (state: CoreLoweringState) =
    match unspanArgument(argument) with
        | ExprVar(name) ->
            match resourceBindingOf(name)(state) with
                | Some((slot, _typeName)) -> Some((name, slot, resourceStateOf(slot)(state)))
                | None -> None
        | _ -> None

// Stage 0's `MarkResourceArgMoved`: a variable argument naming a live resource is moved.
let markResourceArgumentMoved (argument: Expr) (state: CoreLoweringState) =
    match resourceArgumentState(argument)(state) with
        | Some((_name, slot, None)) -> markResourceReleased(slot)(ResourceMoved)(state)
        | _ -> state

let recursive markResourceArgumentsMoved (arguments: List(Expr)) (state: CoreLoweringState) =
    match arguments with
        | [] -> state
        | argument :: rest ->
            state
            |> markResourceArgumentMoved(argument)
            |> markResourceArgumentsMoved(rest)

// Stage 0's `TryMarkDropped`: an explicit close releases a live resource binding.
let markResourceArgumentClosed (argument: Expr) (state: CoreLoweringState) =
    match resourceArgumentState(argument)(state) with
        | Some((_name, slot, None)) -> markResourceReleased(slot)(ResourceClosed)(state)
        | _ -> state

// Stage 0's `CheckUseAfterDrop`: using a released resource is use-after-move or use-after-close.
let checkResourceArgumentUse (argument: Expr) (state: CoreLoweringState) =
    match resourceArgumentState(argument)(state) with
        | Some((name, _slot, Some(ResourceMoved))) -> Some(ResourceUseAfterMove("Resource '" + name + "' has been moved and can no longer be used here. Passing a resource to a function or storing it in a data structure transfers ownership."))
        | Some((name, _slot, Some(ResourceClosed))) -> Some(ResourceUseAfterClose("Resource '" + name + "' has already been closed. Using a resource after it has been closed is not allowed."))
        | _ -> None

// Stage 0's `CheckExplicitExternalResourceClose`: closing a released resource through its
// destructor is closing a moved one or closing twice.
let checkResourceArgumentClose (argument: Expr) (state: CoreLoweringState) =
    match resourceArgumentState(argument)(state) with
        | Some((name, _slot, Some(ResourceMoved))) -> Some(ResourceUseAfterMove("Resource '" + name + "' has been moved and can no longer be closed here. Ownership was transferred when it was passed to a function or stored in a data structure."))
        | Some((name, _slot, Some(ResourceClosed))) -> Some(ResourceDoubleClose("Resource '" + name + "' has already been closed. Closing a resource twice is not allowed."))
        | _ -> None

// An external call's resource contract per input parameter (stage 0's
// `CheckExternalResourceArgument`/`ApplyExternalResourceTransfer`): a `consume` parameter of the
// resource's own destructor closes the argument, any other `consume` moves it, a `borrow` only
// reads it, and each is checked against the binding's state first.
let recursive externalInputParameters (parameters: List(ExternalParameterAbi)) =
    match parameters with
        | [] -> []
        | parameter :: rest ->
            if isOutParameterAbi(parameter)
            then externalInputParameters(rest)
            else parameter :: externalInputParameters(rest)

let externalParameterOwnership (parameter: ExternalParameterAbi) =
    match parameter with
        | ExternalParameterAbi { source = ExternalParameterTyping { ownership = ownership } } -> ownership

let recursive checkExternalResourceArguments (abi: ExternalFunctionAbi) (parameters: List(ExternalParameterAbi)) (arguments: List(Expr)) (state: CoreLoweringState) =
    match (parameters, arguments) with
        | (parameter :: parameterRest, argument :: argumentRest) ->
            match (externalParameterOwnership(parameter), abi.destructorForResource) with
                | (ExternalOwnershipConsume, Some(_resource)) ->
                    match checkResourceArgumentClose(argument)(state) with
                        | Some(error) -> Some(error)
                        | None -> checkExternalResourceArguments(abi)(parameterRest)(argumentRest)(state)
                | (ExternalOwnershipConsume, None) ->
                    match checkResourceArgumentUse(argument)(state) with
                        | Some(error) -> Some(error)
                        | None -> checkExternalResourceArguments(abi)(parameterRest)(argumentRest)(state)
                | (ExternalOwnershipBorrow, _destructor) ->
                    match checkResourceArgumentUse(argument)(state) with
                        | Some(error) -> Some(error)
                        | None -> checkExternalResourceArguments(abi)(parameterRest)(argumentRest)(state)
                | _ -> checkExternalResourceArguments(abi)(parameterRest)(argumentRest)(state)
        | _ -> None

let recursive applyExternalResourceTransfers (abi: ExternalFunctionAbi) (parameters: List(ExternalParameterAbi)) (arguments: List(Expr)) (state: CoreLoweringState) =
    match (parameters, arguments) with
        | (parameter :: parameterRest, argument :: argumentRest) ->
            match (externalParameterOwnership(parameter), abi.destructorForResource) with
                | (ExternalOwnershipConsume, Some(_resource)) ->
                    state
                    |> markResourceArgumentClosed(argument)
                    |> applyExternalResourceTransfers(abi)(parameterRest)(argumentRest)
                | (ExternalOwnershipConsume, None) ->
                    state
                    |> markResourceArgumentMoved(argument)
                    |> applyExternalResourceTransfers(abi)(parameterRest)(argumentRest)
                | _ -> applyExternalResourceTransfers(abi)(parameterRest)(argumentRest)(state)
        | _ -> state

let applyLoweredExternalResourceTransfers (abi: ExternalFunctionAbi) (arguments: List(Expr)) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state } ->
            lowered with state = applyExternalResourceTransfers(abi)(externalInputParameters(abi.parameters))(arguments)(state)

// Whether a builtin reads its first argument as a resource, closes it, or neither.
type BuiltinResourceRole =
    | BuiltinUsesResource
    | BuiltinClosesResource
    | BuiltinNoResource

let builtinResourceRole (kind: CoreBuiltinKind) =
    match kind with
        | CoreFileReadChunk -> BuiltinUsesResource
        | CoreFileReadLine -> BuiltinUsesResource
        | CoreFileClose -> BuiltinClosesResource
        | CoreProcessWriteStdin -> BuiltinUsesResource
        | CoreProcessReadStdoutLine -> BuiltinUsesResource
        | CoreProcessReadStderrLine -> BuiltinUsesResource
        | CoreProcessWaitForExit -> BuiltinUsesResource
        | CoreProcessKill -> BuiltinUsesResource
        | _ -> BuiltinNoResource

// Which of a let-bound lambda's parameters a call through it only borrows: stage 0's parameter
// ownership summary, computed from the lambda's body. An unknown callee consumes everything.
let recursive lookupLetLambda (name: Str) (lambdas: List((Str, List(Str), Expr))) =
    match lambdas with
        | [] -> None
        | (candidate, parameters, body) :: rest ->
            if candidate == name
            then Some((parameters, body))
            else lookupLetLambda(name)(rest)

let recursive parameterAtIndex (index: Int) (ownership: List((Str, ParameterOwnership))) =
    match ownership with
        | [] -> Consumed
        | (_parameter, kind) :: rest ->
            if index == 0
            then kind
            else parameterAtIndex(index - 1)(rest)

let recursive markConsumedArguments (arguments: List(Expr)) (index: Int) (ownership: List((Str, ParameterOwnership))) (state: CoreLoweringState) =
    match arguments with
        | [] -> state
        | argument :: rest ->
            match parameterAtIndex(index)(ownership) with
                | Borrowed -> markConsumedArguments(rest)(index + 1)(ownership)(state)
                | Consumed ->
                    state
                    |> markResourceArgumentMoved(argument)
                    |> markConsumedArguments(rest)(index + 1)(ownership)

// Positionally overlays the whole-program verdict on the single-function one: a parameter the
// fixpoint proved borrowed stays a borrow where the single-function summary saw a consuming
// hand-off; every other parameter keeps its single-function classification.
let recursive overlayProvenBorrows (proven: List((Str, ParameterOwnership))) (ownership: List((Str, ParameterOwnership))) =
    match (proven, ownership) with
        | ((_provenParameter, Borrowed) :: provenRest, (parameter, _kind) :: rest) -> (parameter, Borrowed) :: overlayProvenBorrows(provenRest)(rest)
        | (_provenEntry :: provenRest, entry :: rest) -> entry :: overlayProvenBorrows(provenRest)(rest)
        | (_, remaining) -> remaining

let recursive sameParameterNames (proven: List((Str, ParameterOwnership))) (parameters: List(Str)) =
    match (proven, parameters) with
        | ([], []) -> true
        | ((provenParameter, _kind) :: provenRest, parameter :: rest) -> provenParameter == parameter && sameParameterNames(provenRest)(rest)
        | _ -> false

let recursive registeredTopLevelName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || registeredTopLevelName(name)(rest)

// Stage 0's open-world hand-off approval: the whole-program inspect-only fixpoint
// (`inferProgramParameterOwnership`) is consulted for a callee that is a registered top-level
// function whose parameter chain is the one the fixpoint classified, so a hand-off to a proven
// inspecting helper stays a borrow; a callee the fixpoint did not resolve (a local lambda, or a
// name shadowing a registered function with a different parameter chain) keeps the
// single-function verdict.
let provenParameterOwnership (callee: Str) (parameters: List(Str)) (ownership: List((Str, ParameterOwnership))) (state: CoreLoweringState) =
    match lookupProgramParameterOwnership(callee)(state.programParameterOwnership) with
        | Some(proven) ->
            if registeredTopLevelName(callee)(state.topLevelNames) && sameParameterNames(proven)(parameters)
            then overlayProvenBorrows(proven)(ownership)
            else ownership
        | None -> ownership

// Stage 0's per-argument `borrowsOnly` decision for a general call: an argument naming a live
// resource moves unless the callee is a let-bound lambda proven to only read that parameter,
// by its own summary or by the whole-program fixpoint.
let markCallArgumentsMoved (spine: CoreCallSpine) (state: CoreLoweringState) =
    match unspanArgument(spine.root) with
        | ExprVar(callee) ->
            match lookupLetLambda(callee)(state.letLambdas) with
                | Some((parameters, body)) ->
                    state
                    |> provenParameterOwnership(callee)(parameters)(classifyParameterOwnership(parameters)(body)([]))
                    |> (given (ownership) -> markConsumedArguments(spine.arguments)(0)(ownership)(state))
                | None -> markResourceArgumentsMoved(spine.arguments)(state)
        | _ -> markResourceArgumentsMoved(spine.arguments)(state)

// Stage 0's known-call result: a single application of a let-bound function whose body result
// is reference-counted yields a newly produced reference-counted temp.
let recursive lookupLetLambdaLabel (name: Str) (labels: List((Str, Str))) =
    match labels with
        | [] -> None
        | (candidate, label) :: rest ->
            if candidate == name
            then Some(label)
            else lookupLetLambdaLabel(name)(rest)

let markKnownCallResult (spine: CoreCallSpine) (lowered: LoweredCoreValue) =
    match (unspanArgument(spine.root), spine.arguments, lowered) with
        | (ExprVar(callee), _argument :: [], LoweredCoreValue { state = state, error = None }) ->
            match lookupLetLambdaLabel(callee)(state.letLambdaLabels) with
                | Some(label) ->
                    if bodyReturnsRuntimeManaged(label)(state)
                    then markLoweredRuntimeTemp(lowered)
                    else lowered
                | None -> lowered
        | _ -> lowered

let markLoweredCallArgumentsMoved (spine: CoreCallSpine) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state } -> lowered with state = markCallArgumentsMoved(spine)(state)

// Stage 0's `EmitResourceCleanup`: a live resource binding is closed at its scope exit by a
// `CleanupResource` naming its type and, for a declared external resource, its destructor; a
// closed or moved one is left alone.
let emitResourceCleanup (typeName: Str) (slot: Int) (state: CoreLoweringState) =
    match resourceStateOf(slot)(state) with
        | Some(_kind) -> state
        | None ->
            match freshTemp(state) with
                | FreshTemp { state = loadState, temp = loadTemp } ->
                    loadState
                    |> emit(LoadLocal(loadTemp)(slot))
                    |> emit(state
                    |> declaredResourceDestructor(typeName)
                    |> CleanupResource(loadTemp)(typeName))

// Stage 0's closure-capture transfer for resources: a live resource a closure captures is
// reachable through the closure after this scope, so it moves into the closure instead of being
// closed at the scope exit.
let recursive markCapturedResourcesMoved (captures: List(CoreBinding)) (state: CoreLoweringState) =
    match captures with
        | [] -> state
        | CoreBinding { name = name } :: rest ->
            match resourceBindingOf(name)(state) with
                | Some((slot, _typeName)) ->
                    match resourceStateOf(slot)(state) with
                        | None ->
                            state
                            |> markResourceReleased(slot)(ResourceMoved)
                            |> markCapturedResourcesMoved(rest)
                        | Some(_kind) -> markCapturedResourcesMoved(rest)(state)
                | None -> markCapturedResourcesMoved(rest)(state)

// Stage 0's compiler-inferred borrowing (`LowerVar`): reading an owned binding yields a `Borrow`
// alias of the loaded value, so the owning scope keeps the drop obligation. Ownership is decided
// from the binding's resolved type at the read, since a pattern binding's type is a fresh variable
// when it is bound.
// The read of a reference-counted `let` binding in the position its scope hands it out through
// takes the slot's reference: the temp is newly produced for its consumer and the slot no longer
// owns anything at the scope exit.
let transfersSlot (slot: Int) (state: CoreLoweringState) =
    match consumerRequestOf(state) with
        | ConsumerRequest { transferSlot = Some(transfer) } -> transfer == slot
        | _ -> false

let recursive releaseRuntimeOwner (slot: Int) (owners: List((Int, Bool))) =
    match owners with
        | [] -> []
        | (candidate, owned) :: rest ->
            if candidate == slot
            then (candidate, false) :: rest
            else (candidate, owned) :: releaseRuntimeOwner(slot)(rest)

let transferRuntimeOwner (slot: Int) (temp: Int) (state: CoreLoweringState) =
    state
    |> markRuntimeTemp(temp)(RuntimeNewlyProduced)
    |> (given (transferred: CoreLoweringState) -> transferred with runtimeOwners = releaseRuntimeOwner(slot)(transferred.runtimeOwners))

let recursive lookupRuntimeOwner (slot: Int) (owners: List((Int, Bool))) =
    match owners with
        | [] -> None
        | (candidate, owned) :: rest ->
            if candidate == slot
            then Some(owned)
            else lookupRuntimeOwner(slot)(rest)

let runtimeOwnerStateOf (slot: Int) (state: CoreLoweringState) = lookupRuntimeOwner(slot)(state.runtimeOwners)

let finishOwnedRead ownedRead temp semanticType state =
    match state with
        | CoreLoweringState { constructorLayouts = layouts } ->
            if ownedRead
            then
                match ownedTypeNameOf(resolveType(state)(semanticType))(layouts) with
                    | Some(_typeName) ->
                        match freshTemp(state) with
                            | FreshTemp { state = borrowState, temp = borrowTemp } ->
                                borrowState
                                |> emit(Borrow(borrowTemp)(temp))
                                |> success(borrowTemp)(semanticType)
                    | None -> success(temp)(semanticType)(state)
            else success(temp)(semanticType)(state)

let recursive lowerVariable name state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            match lookupBinding(name)(bindings) with
                | None -> failure(state)(UnknownLoweringBinding(name))
                | Some(binding) -> lowerBoundVariable(binding)(state)
and lowerBoundVariable binding state =
    match instantiateBinding(binding)(state) with
        | InstantiatedBinding { state = instantiatedState, semanticType = semanticType } ->
            match binding with
                | CoreBinding { location = CoreSelf(label, environmentSize) } ->
                    match freshTemp(instantiatedState) with
                        | FreshTemp { state = closureAllocated, temp = closureTemp } ->
                            match freshTemp(closureAllocated) with
                                | FreshTemp { state = closureState, temp = environmentTemp } ->
                                    closureState
                                    |> emit(LoadLocal(environmentTemp)(0))
                                    |> emit(MakeClosure(
                                        closureTemp,
                                        label,
                                        environmentTemp,
                                        environmentSize,
                                        false,
                                        false,
                                        false
                                    ))
                                    |> success(closureTemp)(semanticType)
                | CoreBinding { location = CoreLocal(slot), ownedRead = ownedRead } ->
                    match freshTemp(instantiatedState) with
                        | FreshTemp { state = tempState, temp = temp } ->
                            if transfersSlot(slot)(tempState)
                            then
                                tempState
                                |> emit(LoadLocal(temp)(slot))
                                |> transferRuntimeOwner(slot)(temp)
                                |> success(temp)(semanticType)
                            else
                                tempState
                                |> emit(LoadLocal(temp)(slot))
                                |> finishOwnedRead(ownedRead)(temp)(semanticType)
                | CoreBinding { location = CoreEnvironment(index) } ->
                    match freshTemp(instantiatedState) with
                        | FreshTemp { state = tempState, temp = temp } ->
                            tempState
                            |> emit(LoadEnv(temp)(index))
                            |> success(temp)(semanticType)

let addBinding name scheme location state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            let binding = CoreBinding(name = name, scheme = scheme, location = location, ownedRead = false)
            in state with bindings = binding :: bindings

let addOwnedBinding name scheme location state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            let binding = CoreBinding(name = name, scheme = scheme, location = location, ownedRead = true)
            in state with bindings = binding :: bindings

let restoreBindings bindings (state: CoreLoweringState) = state with bindings = bindings

let restoreLoweredBindings outerBindings lowered =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = error } ->
            LoweredCoreValue(
                state = restoreBindings(outerBindings)(state),
                temp = temp,
                semanticType = semanticType,
                error = error
            )

// The type variables still owed a deferred `+` resolution must never be generalized: a scheme
// quantifying one hands every call site a fresh instantiation, leaving the recorded variable
// unresolvable and the speculative `AddInt` grounded to the wrong form. The pending types —
// resolved first, since the recorded variable may already stand for another — are bundled into
// one unquantified scheme treated as part of the environment, keeping them monomorphic so the
// first concrete use (a call argument, a `""` seed) binds them for the whole group.
let recursive resolvedPendingOperatorTypes state types acc =
    match types with
        | [] -> acc
        | semanticType :: rest -> resolvedPendingOperatorTypes(state)(rest)(resolveType(state)(semanticType) :: acc)

let recursive pendingOperatorTypes pending acc =
    match pending with
        | [] -> acc
        | (_target, semanticType) :: rest -> pendingOperatorTypes(rest)(semanticType :: acc)

let recursive sealedOperatorTypes sealed acc =
    match sealed with
        | [] -> acc
        | (_label, _target, semanticType) :: rest -> sealedOperatorTypes(rest)(semanticType :: acc)

let pendingOperatorScheme state =
    match state with
        | CoreLoweringState { pendingOperatorDefaults = pending, sealedOperatorDefaults = sealed } ->
            TypeScheme(
                quantified = [],
                body = []
                |> resolvedPendingOperatorTypes(state)([]
                |> sealedOperatorTypes(sealed)
                |> pendingOperatorTypes(pending))
                |> SemTuple,
                constraints = []
            )

// A `let` bound to a newly produced reference-counted value owns it: the value temp is handed on
// to the slot, and a body that returns the binding itself (through nested lets) hands the slot's
// reference on again instead of borrowing it, stage 0's tail-forwarded binding result.
let adoptRuntimeLetValue (valueTemp: Int) (slot: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(valueTemp)(state) with
        | Some(RuntimeNewlyProduced) ->
            state
            |> markRuntimeTemp(valueTemp)(RuntimeTransferred)
            |> (given (adopted: CoreLoweringState) -> adopted with runtimeOwners = (slot, true) :: adopted.runtimeOwners)
        | _ -> state

let recursive isTailForwardedBindingResult (body: Expr) (name: Str) =
    match body with
        | ExprAt(_span, inner) -> isTailForwardedBindingResult(inner)(name)
        | ExprVar(candidate) -> candidate == name
        | ExprLet(nested, _value, nestedBody, _parameters, _annotation, _requirements) -> nested != name && isTailForwardedBindingResult(nestedBody)(name)
        | ExprLetRecursive(nested, _value, nestedBody, _parameters, _annotation, _requirements) -> nested != name && isTailForwardedBindingResult(nestedBody)(name)
        | _ -> false

let letBodyRequest (name: Str) (body: Expr) (valueTemp: Int) (slot: Int) (state: CoreLoweringState) =
    match (runtimeTempStateOf(valueTemp)(state), isTailForwardedBindingResult(body)(name)) with
        | (Some(RuntimeNewlyProduced), true) -> consumerRequestOf(state) with transferSlot = Some(slot)
        | _ -> consumerRequestOf(state)

let lowerStoredLet name body lower outerBindings valueTemp valueType fresh =
    match fresh with
        | FreshLocal { state = state, local = local } ->
            let storedState =
                emit(StoreLocal(local)(valueTemp))(state)
            in
                let scheme =
                    generalize(pendingOperatorScheme(storedState) :: bindingSchemes(outerBindings))(resolveType(storedState)(valueType))([])
                in
                    storedState
                    |> withConsumerRequest(letBodyRequest(name)(body)(valueTemp)(local)(storedState))
                    |> adoptRuntimeLetValue(valueTemp)(local)
                    |> addOwnedBinding(name)(scheme)(CoreLocal(local))
                    |> lower(body)
                    |> restoreLoweredBindings(outerBindings)

let finishLetValue name body lower outerBindings lowered =
    match lowered with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            state
            |> freshLocal
            |> lowerStoredLet(name)(body)(lower)(outerBindings)(temp)(semanticType)

let recursive stripExprAt (expr: Expr) =
    match expr with
        | ExprAt(_span, inner) -> stripExprAt(inner)
        | other -> other

// Stage 0's parser attaches no location between `in` and a directly-chained `let`, so every
// frame store in a `let ... in let ... in body` chain carries the chain head's span (its
// LowerSequentialBindingChain walks the chain without touching the ambient diagnostic span).
// This parser wraps the chained let in its own `ExprAt`; dropping that wrapper here reproduces
// stage 0's spans exactly — the chained let's value and body keep their own inner `ExprAt`s, so
// only the frame store's location is affected.
let stripChainedLetAt body =
    match body with
        | ExprAt(_span, inner) ->
            match inner with
                | ExprLet(_name, _value, _body, _parameters, _annotation, _requirements) -> inner
                | ExprLetRecursive(_name, _value, _body, _parameters, _annotation, _requirements) -> inner
                | _ -> body
        | _ -> body

let recursive patternBindsName (name: Str) (pattern: Pattern) =
    match pattern with
        | PatternAt(_span, inner) -> patternBindsName(name)(inner)
        | PatternVar(candidate) -> candidate == name
        | PatternCons(head, tail) -> patternBindsName(name)(head) || patternBindsName(name)(tail)
        | PatternTuple(elements) -> patternsBindName(name)(elements)
        | PatternConstructor(_constructor, arguments) -> patternsBindName(name)(arguments)
        | PatternRecord(_typeName, fields) -> fieldPatternsBindName(name)(fields)
        | PatternAs(inner, alias) -> alias == name || patternBindsName(name)(inner)
        | PatternOr(alternatives) -> patternsBindName(name)(alternatives)
        | _ -> false
and patternsBindName (name: Str) (patterns: List(Pattern)) =
    match patterns with
        | [] -> false
        | pattern :: rest -> patternBindsName(name)(pattern) || patternsBindName(name)(rest)
and fieldPatternsBindName (name: Str) (fields: List((Str, Pattern))) =
    match fields with
        | [] -> false
        | (_field, pattern) :: rest -> patternBindsName(name)(pattern) || fieldPatternsBindName(name)(rest)

// Stage 0's direct-callee analysis for one binding: whether `name` is used in `body` only as the
// callee of an application, the condition under which its lambda value is a stack closure. A
// binding that shadows the name ends the walk of its scope, and an expression form the walk does
// not know counts as a non-callee use, which only ever keeps a closure on the heap.
let recursive nameHasNonCalleeUse (name: Str) (expr: Expr) (asCallee: Bool) =
    match expr with
        | ExprAt(_span, inner) -> nameHasNonCalleeUse(name)(inner)(asCallee)
        | ExprInt(_value) -> false
        | ExprBigInt(_value) -> false
        | ExprUInt(_value, _bitWidth, _suffix) -> false
        | ExprFloat(_value, _suffix) -> false
        | ExprString(_value) -> false
        | ExprRune(_value) -> false
        | ExprBool(_value) -> false
        | ExprVar(candidate) -> candidate == name && asCallee == false
        | ExprQualifiedVar(_moduleName, _memberName) -> false
        | ExprCall(function, argument, _isSugar, _layout) -> nameHasNonCalleeUse(name)(function)(true) || nameHasNonCalleeUse(name)(argument)(false)
        | ExprLambda(parameter, body, _annotation) -> parameter != name && nameHasNonCalleeUse(name)(body)(false)
        | ExprLet(bound, value, body, _parameters, _annotation, _requirements) -> nameHasNonCalleeUse(name)(value)(false) || bound != name && nameHasNonCalleeUse(name)(body)(false)
        | ExprLetResult(bound, value, body) -> nameHasNonCalleeUse(name)(value)(false) || bound != name && nameHasNonCalleeUse(name)(body)(false)
        | ExprLetRecursive(bound, value, body, _parameters, _annotation, _requirements) -> bound != name && (nameHasNonCalleeUse(name)(value)(false) || nameHasNonCalleeUse(name)(body)(false))
        | ExprIf(condition, thenBranch, elseBranch) -> nameHasNonCalleeUse(name)(condition)(false) || nameHasNonCalleeUse(name)(thenBranch)(false) || nameHasNonCalleeUse(name)(elseBranch)(false)
        | ExprAdd(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprSubtract(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprMultiply(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprDivide(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprModulo(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprBitwiseAnd(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprBitwiseOr(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprBitwiseXor(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprShiftLeft(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprShiftRight(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprLogicalAnd(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprLogicalOr(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprGreaterThan(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprLessThan(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprGreaterOrEqual(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprLessOrEqual(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprEqual(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprNotEqual(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprResultPipe(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprResultMapErrorPipe(left, right) -> eitherHasNonCalleeUse(name)(left)(right)
        | ExprCons(head, tail) -> eitherHasNonCalleeUse(name)(head)(tail)
        | ExprBitwiseNot(operand) -> nameHasNonCalleeUse(name)(operand)(false)
        | ExprLogicalNot(operand) -> nameHasNonCalleeUse(name)(operand)(false)
        | ExprTuple(elements) -> anyHasNonCalleeUse(name)(elements)
        | ExprList(elements, _isMultiline) -> anyHasNonCalleeUse(name)(elements)
        | ExprRecord(_typeName, fields, _isMultiline) -> anyFieldHasNonCalleeUse(name)(fields)
        | ExprRecordUpdate(record, fields) -> nameHasNonCalleeUse(name)(record)(false) || anyFieldHasNonCalleeUse(name)(fields)
        | ExprMatch(scrutinee, arms, _position) -> nameHasNonCalleeUse(name)(scrutinee)(false) || anyArmHasNonCalleeUse(name)(arms)
        | _ -> true
and eitherHasNonCalleeUse (name: Str) (left: Expr) (right: Expr) = nameHasNonCalleeUse(name)(left)(false) || nameHasNonCalleeUse(name)(right)(false)
and anyHasNonCalleeUse (name: Str) (expressions: List(Expr)) =
    match expressions with
        | [] -> false
        | expression :: rest -> nameHasNonCalleeUse(name)(expression)(false) || anyHasNonCalleeUse(name)(rest)
and anyFieldHasNonCalleeUse (name: Str) (fields: List((Str, Expr))) =
    match fields with
        | [] -> false
        | (_field, expression) :: rest -> nameHasNonCalleeUse(name)(expression)(false) || anyFieldHasNonCalleeUse(name)(rest)
and anyArmHasNonCalleeUse (name: Str) (arms: List((Pattern, Expr, Maybe(Expr)))) =
    match arms with
        | [] -> false
        | (pattern, body, guard) :: rest ->
            if patternBindsName(name)(pattern)
            then anyArmHasNonCalleeUse(name)(rest)
            else guardHasNonCalleeUse(name)(guard) || nameHasNonCalleeUse(name)(body)(false) || anyArmHasNonCalleeUse(name)(rest)
and guardHasNonCalleeUse (name: Str) (guard: Maybe(Expr)) =
    match guard with
        | None -> false
        | Some(condition) -> nameHasNonCalleeUse(name)(condition)(false)

let nameUsedOnlyAsDirectCallee (name: Str) (body: Expr) = nameHasNonCalleeUse(name)(body)(false) == false

// A nested `let`'s arena bracket, stage 0's exact per-binding discipline: `SaveArenaState`
// before the value, and after the whole body the spill of an owned binding's result, its
// release anchor, and the reset the scope rule allows (an inner chained `let` opens and closes
// its own bracket inside this one, so the pairs close LIFO before the enclosing binding's
// store). Every `let` is bracketed; whether its window is reset is decided from the body's type
// when the scope closes, so no syntactic proof is needed up front.
// The owned type name of a lowered `let` value (`ownedTypeNameOf`), `None` when the value is a
// copy type; an owned binding's scope carries a drop obligation the closing bracket discharges.
// The owned type name a `let`'s value releases under, a declared external resource counting as
// owned by its opaque name.
let loweredValueOwnedTypeName lowered =
    match lowered with
        | LoweredCoreValue { state = state, semanticType = semanticType, error = None } ->
            match resolveType(state)(semanticType) with
                | SemOpaque(name) ->
                    if isDeclaredResourceName(name)(state)
                    then Some(name)
                    else None
                | resolved -> ownedTypeNameOf(resolved)(state.constructorLayouts)
        | _ -> None

// The lexical release of an owned `let` binding at its scope exit, stage 0's
// `EmitOwnedValueDrop`: the owner is loaded back and released with an `RcDrop` naming its slot,
// which `PerceusLifetimePlacement` later moves to the binding's last use on each path. A closure
// is released with `CleanupResource` instead, which stays at the scope exit.
let emitOwnedLetRelease typeName ownerSlot state =
    if isResourceTypeNameIn(typeName)(state)
    then emitResourceCleanup(typeName)(ownerSlot)(state)
    else
        match runtimeOwnerStateOf(ownerSlot)(state) with
            | Some(false) -> state
            | runtimeOwner ->
                match freshTemp(state) with
                    | FreshTemp { state = loadState, temp = loadTemp } ->
                        loadState
                        |> emit(LoadLocal(loadTemp)(ownerSlot))
                        |> (given (loaded) ->
                            if typeName == "Function"
                            then
                                emit(CleanupResource(loadTemp)(typeName)(None))(loaded)
                            else
                                emit(RcDrop(loadTemp)(typeName)(ownerSlot)(runtimeOwner == Some(true))(false)(None))(loaded))

// The coverage and reachability rules live in `TypeInference.ash`'s `matchCoverageError` — the
// exact checker the project-inference path runs — fed here with a minimal `TypeEnvironment`
// carrying the live constructor layouts (intrinsic and user-declared alike, deep-copied out of
// the long-lived state), so the single-file lowering path reports the same non-exhaustive-match,
// unreachable-arm, and mixed-ADT diagnostics with stage 0's wording.
let recursive constructorInferenceDefinitionsFromLayouts layouts =
    match layouts with
        | [] -> []
        | CoreConstructorLayout { name = name, scheme = scheme, fieldNames = fieldNames } :: rest ->
            ConstructorInferenceDefinition(
                name = Ashes.Internal.deepCopy(name),
                scheme = Ashes.Internal.deepCopy(scheme),
                fieldNames = Ashes.Internal.deepCopy(fieldNames)
            ) :: constructorInferenceDefinitionsFromLayouts(rest)

let coverageEnvironment state =
    match state with
        | CoreLoweringState { constructorLayouts = layouts } -> emptyTypeEnvironment(Unit) with constructors = constructorInferenceDefinitionsFromLayouts(layouts)

let recursive schemeResultName (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(_, result, _) -> schemeResultName(result)
        | SemNamed(_, name, _) -> Some(name)
        | _ -> None

let emitRestoreAndReclaim cursorSlot endSlot preRestoreSlot state =
    state
    |> emit(RestoreArenaState(cursorSlot)(endSlot)(preRestoreSlot)(false))
    |> emit(ReclaimArenaChunks(endSlot)(preRestoreSlot)(false))

// A type variable that a deferred `+` default owns resolves to `Int` at finalization, the type
// the deferred add itself falls back to.
let recursive operatorDefaultedVariables (types: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> []
        | semanticType :: rest ->
            match resolveType(state)(semanticType) with
                | SemVariable(id) -> id :: operatorDefaultedVariables(rest)(state)
                | _ -> operatorDefaultedVariables(rest)(state)

// Whether a scope's or a call's result survives the arena reset that closes it: stage 0's
// `CanArenaReset`, a scalar seen through a zero-cost wrapper. A type variable that a deferred `+`
// default owns is the `Int` that default resolves to, which stage 0 already knows at that point.
let resultSurvivesReset (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemVariable(id) ->
            []
            |> sealedOperatorTypes(state.sealedOperatorDefaults)
            |> pendingOperatorTypes(state.pendingOperatorDefaults)
            |> (given (types) ->
                state
                |> operatorDefaultedVariables(types)
                |> containsInt(id))
        | SemNamed(_symbolId, name, _arguments) ->
            match zeroCostPayloadType(name)(state.constructorLayouts) with
                | Some(payload) ->
                    payload
                    |> resolveType(state)
                    |> canArenaResetLayout
                | None -> false
        | resolved -> canArenaResetLayout(resolved)

// The closing reset of a scope: the pre-restore end slot is allocated either way, as stage 0
// does, and the arena is restored and reclaimed only when the scope's result survives it. A heap
// result leaves the window open, since the copy-out that would preserve it is not ported yet.
let closeScopeForResult (resultTemp: Int) (resultType: SemanticType) cursorSlot endSlot state =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = preRestoreSlot } ->
            if resultSurvivesReset(resultType)(allocated) || isRuntimeTemp(resultTemp)(allocated)
            then emitRestoreAndReclaim(cursorSlot)(endSlot)(preRestoreSlot)(allocated)
            else allocated

// What a scope's heap result needs to cross the closing reset, stage 0's `GetCopyOutKind`: a
// string or `Bytes` copies by its dynamic size, a list over scalar elements walks its spine, and a
// named type copies its fixed cell when every constructor has the same scalar-only arity. A
// closure, a tuple, and every other layout have no copy-out and leave the window open.
type ScopeCopyOut =
    | ShallowScopeCopyOut(Int)
    | ListScopeCopyOut

let layoutResultName layout =
    match layout with
        | CoreConstructorLayout { scheme = TypeScheme { body = body } } -> schemeResultName(body)

let recursive firstLayoutOfType (name: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> None
        | layout :: rest ->
            if layoutResultName(layout) == Some(name)
            then Some(layout)
            else firstLayoutOfType(name)(rest)

// The cell size of a same-arity ADT: one word per field, behind a tag word unless the type is
// tagless.
let shallowAdtCopySizeBytes (name: Str) (state: CoreLoweringState) =
    match firstLayoutOfType(name)(state.constructorLayouts) with
        | Some(CoreConstructorLayout { tagless = tagless } as layout) ->
            layout
            |> constructorArity
            |> adtAllocationSizeBytes(tagless)
        | None -> adtAllocationSizeBytes(false)(0)

let scopeCopyOutOf (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> Some(ShallowScopeCopyOut(-1))
        | SemBytes -> Some(ShallowScopeCopyOut(-1))
        | SemList(element) ->
            if element
            |> resolveType(state)
            |> canArenaResetLayout
            then Some(ListScopeCopyOut)
            else None
        | SemNamed(_symbolId, name, _arguments) as named ->
            match state
            |> coverageEnvironment
            |> classifyHeapLayout(named) with
                | HeapLayoutFacts { structuralCopy = ShallowCopy } ->
                    Some(state
                    |> shallowAdtCopySizeBytes(name)
                    |> ShallowScopeCopyOut)
                | _ -> None
        | _ -> None

let scopeCopyOutInstruction copyOut copyTemp resultTemp =
    match copyOut with
        | ShallowScopeCopyOut(staticSizeBytes) -> CopyOutArena(copyTemp)(resultTemp)(staticSizeBytes)(true)(RcNormalization)(None)
        | ListScopeCopyOut -> CopyOutList(copyTemp)(resultTemp)(InlineListHead)(true)(RcNormalization)

// Restores the arena, copies the result past the reset into a fresh runtime-managed temp, and
// reclaims the chunks, stage 0's `TryEmitScopeCopyOut` with the RC-normalizing copy.
let emitScopeCopyOut copyOut resultTemp cursorSlot endSlot preRestoreSlot state =
    match state
    |> emit(RestoreArenaState(cursorSlot)(endSlot)(preRestoreSlot)(false))
    |> freshTemp with
        | FreshTemp { state = restored, temp = copyTemp } ->
            restored
            |> emit(scopeCopyOutInstruction(copyOut)(copyTemp)(resultTemp))
            |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
            |> emit(ReclaimArenaChunks(endSlot)(preRestoreSlot)(false))
            |> (given (closed) -> (closed, Some(copyTemp)))

// The closing reset of a scope that owned and released a binding, stage 0's `PopOwnershipScope`:
// a surviving result resets the arena, a heap result with a copy-out kind is copied past the
// reset (the copy temp replaces the result), and any other heap result leaves the window open.
let closeOwnedScopeForResult (resultTemp: Int) (resultType: SemanticType) cursorSlot endSlot state =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = preRestoreSlot } ->
            if resultSurvivesReset(resultType)(allocated) || isRuntimeTemp(resultTemp)(allocated)
            then (emitRestoreAndReclaim(cursorSlot)(endSlot)(preRestoreSlot)(allocated), None)
            else
                match scopeCopyOutOf(resultType)(allocated) with
                    | None -> (allocated, None)
                    | Some(copyOut) -> emitScopeCopyOut(copyOut)(resultTemp)(cursorSlot)(endSlot)(preRestoreSlot)(allocated)

// Closes a `let`'s arena bracket and returns the closed state with the result temp. A scope that
// owns its binding spills the body result to a slot, releases the binding, restores, and reloads
// the result afterwards (stage 0's result preservation: the release could otherwise overwrite the
// result temp); a scope owning nothing restores and returns the body temp directly.
let closeOwnedLetBracket ownedTypeName ownerSlot cursorSlot endSlot resultTemp resultType state =
    match (ownedTypeName, runtimeOwnerStateOf(ownerSlot)(state)) with
        | (Some(_transferred), Some(false)) -> (closeScopeForResult(resultTemp)(resultType)(cursorSlot)(endSlot)(state), resultTemp)
        | (Some(typeName), _owned) ->
            match freshLocal(state) with
                | FreshLocal { state = resultAllocated, local = resultSlot } ->
                    match resultAllocated
                    |> emit(StoreLocal(resultSlot)(resultTemp))
                    |> emitOwnedLetRelease(typeName)(ownerSlot)
                    |> closeOwnedScopeForResult(resultTemp)(resultType)(cursorSlot)(endSlot) with
                        | (closed, Some(copyTemp)) -> (closed, copyTemp)
                        | (closed, None) ->
                            match freshTemp(closed) with
                                | FreshTemp { state = reloadState, temp = reloadTemp } ->
                                    (emit(LoadLocal(reloadTemp)(resultSlot))(reloadState), reloadTemp)
        | (None, _owned) -> (closeScopeForResult(resultTemp)(resultType)(cursorSlot)(endSlot)(state), resultTemp)

// `finishLetValue` with the binding's own slot exposed, for the bracketed closers that release it.
let finishLetValueInSlot name body lower outerBindings lowered =
    match lowered with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> (failure(failedState)(error), -1)
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            match freshLocal(state) with
                | FreshLocal { local = local } as fresh -> (lowerStoredLet(name)(body)(lower)(outerBindings)(temp)(semanticType)(fresh), local)

// Stage 0's `IsRuntimeRcStringProducer`: `+` or a fully applied call to a builtin declared to
// produce a fresh string — the expressions a runtime-string request can place on the
// reference-counted heap.
let isFreshStringBuiltinKind (kind: CoreBuiltinKind) =
    match kind with
        | CoreTextFromInt -> true
        | CoreTextFromFloat -> true
        | CoreTextFormatFloat -> true
        | CoreBigIntToString -> true
        | CoreTextToHex -> true
        | CoreTextAsciiCase(_upper) -> true
        | CoreRuneToText -> true
        | CoreBytesSubText -> true
        | _ -> false

// The root and argument count of a call spine, spans looked through.
let recursive qualifiedCallRoot (expression: Expr) (argumentCount: Int) =
    match expression with
        | ExprAt(_span, inner) -> qualifiedCallRoot(inner)(argumentCount)
        | ExprCall(function, _argument, _isSugar, _layout) -> qualifiedCallRoot(function)(argumentCount + 1)
        | ExprQualifiedVar(moduleName, memberName) -> Some((moduleName, memberName, argumentCount))
        | _ -> None

let freshStringBuiltinCallKind (expression: Expr) (state: CoreLoweringState) =
    match qualifiedCallRoot(expression)(0) with
        | Some((moduleName, memberName, argumentCount)) ->
            match builtinLayout(moduleName)(memberName)(state) with
                | Some(CoreBuiltinLayout { kind = kind } as layout) ->
                    if isFreshStringBuiltinKind(kind) && argumentCount == builtinArity(layout)
                    then Some(kind)
                    else None
                | None -> None
        | None -> None

let isRuntimeRcStringProducer (expression: Expr) (state: CoreLoweringState) =
    match unspanArgument(expression) with
        | ExprAdd(_left, _right) -> true
        | _ ->
            match freshStringBuiltinCallKind(expression)(state) with
                | Some(_kind) -> true
                | None -> false

// Stage 0's `IsRuntimeRcClosureCaptureSafeStringProducer`: the producers whose fresh string a
// closure may capture, or a `let` hand on as its result, without an arena copy behind it.
let isCaptureSafeStringProducer (expression: Expr) (state: CoreLoweringState) =
    match unspanArgument(expression) with
        | ExprAdd(_left, _right) -> true
        | _ ->
            match freshStringBuiltinCallKind(expression)(state) with
                | Some(CoreTextFromInt) -> true
                | Some(CoreTextFromFloat) -> true
                | Some(CoreTextToHex) -> true
                | Some(CoreBigIntToString) -> true
                | Some(CoreTextFormatFloat) -> true
                | _ -> false

let isDirectBindingResult (body: Expr) (name: Str) =
    match unspanArgument(body) with
        | ExprVar(candidate) -> candidate == name
        | _ -> false

// Stage 0's `IsImmediateRuntimeStringUse`: the body hands the binding straight to a consumer
// that reads a runtime string (`Ashes.Text.length`/`byteLength`, `Ashes.IO.print`).
let isImmediateRuntimeStringUse (body: Expr) (name: Str) =
    match unspanArgument(body) with
        | ExprCall(function, argument, _isSugar, _layout) ->
            match (unspanArgument(function), isDirectBindingResult(argument)(name)) with
                | (ExprQualifiedVar(moduleName, memberName), true) -> moduleName == "Ashes.Text" && (memberName == "length" || memberName == "byteLength") || moduleName == "Ashes.IO" && memberName == "print"
                | _ -> false
        | _ -> false

// Stage 0's `TryLowerRuntimeRcStringLet`: a `let` whose value is a fresh string producer and
// whose body either returns the binding or hands it straight to a runtime-string consumer places
// the value on the reference-counted heap; the value is otherwise lowered without a request.
let letValueRequest (name: Str) (value: Expr) (body: Expr) (state: CoreLoweringState) =
    (let directEscape = isDirectBindingResult(body)(name)
    in
        if isRuntimeRcStringProducer(value)(state) && (directEscape || isImmediateRuntimeStringUse(body)(name)) && (isCaptureSafeStringProducer(value)(state) || directEscape == false)
        then emptyConsumerRequest with runtimeString = true
        else emptyConsumerRequest)

let lowerArenaBracketedNestedLet name value body lower outerBindings state =
    match freshLocal(state) with
        | FreshLocal { state = cursorAllocated, local = cursorSlot } ->
            match freshLocal(cursorAllocated) with
                | FreshLocal { state = endAllocated, local = endSlot } ->
                    match endAllocated
                    |> withConsumerRequest(letValueRequest(name)(value)(body)(state))
                    |> emit(SaveArenaState(cursorSlot)(endSlot)(false))
                    |> armSourceFunction(name)(value)(nameUsedOnlyAsDirectCallee(name)(body))
                    |> lower(value) with
                        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                        | LoweredCoreValue { error = None } as loweredValue ->
                            match loweredValue
                            |> withLoweredConsumerRequest(consumerRequestOf(state))
                            |> finishLetValueInSlot(name)(stripChainedLetAt(body))(lower)(outerBindings) with
                                | (LoweredCoreValue { state = failedState, error = Some(error) }, _slot) -> failure(failedState)(error)
                                | (LoweredCoreValue { state = bodyState, temp = resultTemp, semanticType = resultType, error = None }, ownerSlot) ->
                                    match closeOwnedLetBracket(loweredValueOwnedTypeName(loweredValue))(ownerSlot)(cursorSlot)(endSlot)(resultTemp)(resultType)(bodyState) with
                                        | (closed, finalTemp) -> success(finalTemp)(resultType)(closed)

let lowerLet name value body lower state =
    match state with
        | CoreLoweringState { bindings = outerBindings } -> lowerArenaBracketedNestedLet(name)(value)(body)(lower)(outerBindings)(state)

let recursive containsName name names =
    match names with
        | [] -> false
        | candidate :: rest ->
            if name == candidate
            then true
            else containsName(name)(rest)

let addFreeName name bound free =
    match (containsName(name)(bound), containsName(name)(free)) with
        | (false, false) -> append(free)([name])
        | _ -> free

let recursive patternBindingNames pattern names =
    match pattern with
        | PatternAt(_span, inner) -> patternBindingNames(inner)(names)
        | PatternVar(name) ->
            if containsName(name)(names)
            then names
            else name :: names
        | PatternCons(head, tail) ->
            names
            |> patternBindingNames(head)
            |> patternBindingNames(tail)
        | PatternTuple(patterns) -> patternListBindingNames(patterns)(names)
        | PatternConstructor(_name, patterns) -> patternListBindingNames(patterns)(names)
        | PatternRecord(_name, fields) -> patternFieldBindingNames(fields)(names)
        | PatternAs(inner, name) -> patternBindingNames(inner)(name :: names)
        | PatternOr([]) -> names
        | PatternOr(first :: _rest) -> patternBindingNames(first)(names)
        | _ -> names
and patternListBindingNames patterns names =
    match patterns with
        | [] -> names
        | pattern :: rest ->
            names
            |> patternBindingNames(pattern)
            |> patternListBindingNames(rest)
and patternFieldBindingNames fields names =
    match fields with
        | [] -> names
        | (_field, pattern) :: rest ->
            names
            |> patternBindingNames(pattern)
            |> patternFieldBindingNames(rest)

let recursive collectFree expression bound free =
    match expression with
        | ExprAt(_span, inner) -> collectFree(inner)(bound)(free)
        | ExprVar(name) -> addFreeName(name)(bound)(free)
        | ExprAdd(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprSubtract(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprMultiply(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprDivide(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprModulo(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprBitwiseAnd(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprBitwiseOr(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprBitwiseXor(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprShiftLeft(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprShiftRight(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprGreaterThan(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprGreaterOrEqual(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprLessThan(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprLessOrEqual(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprEqual(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprNotEqual(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprCons(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprBitwiseNot(operand) -> collectFree(operand)(bound)(free)
        | ExprLogicalNot(operand) -> collectFree(operand)(bound)(free)
        | ExprLogicalAnd(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprLogicalOr(left, right) -> collectFreeBinary(left)(right)(bound)(free)
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) ->
            let valueFree = collectFree(value)(bound)(free)
            in collectFree(body)(name :: bound)(valueFree)
        | ExprLetRecursive(name, value, body, _parameters, _annotation, _requirements) ->
            let recursiveBound = name :: bound
            in
                let valueFree = collectFree(value)(recursiveBound)(free)
                in collectFree(body)(recursiveBound)(valueFree)
        | ExprIf(condition, thenBranch, elseBranch) ->
            let conditionFree = collectFree(condition)(bound)(free)
            in
                let thenFree = collectFree(thenBranch)(bound)(conditionFree)
                in collectFree(elseBranch)(bound)(thenFree)
        | ExprLambda(parameter, body, _annotation) -> collectFree(body)(parameter :: bound)(free)
        | ExprCall(function, argument, _whitespace, _layout) ->
            let functionFree = collectFree(function)(bound)(free)
            in collectFree(argument)(bound)(functionFree)
        | ExprTuple(elements) -> collectFreeExpressions(elements)(bound)(free)
        | ExprList(elements, _isMultiline) -> collectFreeExpressions(elements)(bound)(free)
        | ExprRecord(_name, fields, _isMultiline) -> collectFreeFields(fields)(bound)(free)
        | ExprRecordUpdate(target, fields) ->
            free
            |> collectFree(target)(bound)
            |> collectFreeFields(fields)(bound)
        | ExprMatch(value, cases, _position) ->
            free
            |> collectFree(value)(bound)
            |> collectMatchCasesFree(cases)(bound)
        | _ -> free
and collectFreeBinary left right bound free =
    free
    |> collectFree(left)(bound)
    |> collectFree(right)(bound)
and collectFreeExpressions expressions bound free =
    match expressions with
        | [] -> free
        | expression :: rest ->
            free
            |> collectFree(expression)(bound)
            |> collectFreeExpressions(rest)(bound)
and collectFreeFields fields bound free =
    match fields with
        | [] -> free
        | (_name, expression) :: rest ->
            free
            |> collectFree(expression)(bound)
            |> collectFreeFields(rest)(bound)
and collectMatchCaseFree bound free case =
    match case with
        | (pattern, body, guard) ->
            let caseBound =
                append(patternBindingNames(pattern)([]))(bound)
            in
                let guardFree =
                    match guard with
                        | None -> free
                        | Some(expression) -> collectFree(expression)(caseBound)(free)
                in collectFree(body)(caseBound)(guardFree)
and collectMatchCasesFree cases bound free =
    match cases with
        | [] -> free
        | case :: rest ->
            case
            |> collectMatchCaseFree(bound)(free)
            |> collectMatchCasesFree(rest)(bound)

let recursive capturedBindings names bindings reversed =
    match names with
        | [] -> reverse(reversed)
        | name :: rest ->
            match lookupBinding(name)(bindings) with
                | None -> capturedBindings(rest)(bindings)(reversed)
                | Some(binding) -> capturedBindings(rest)(bindings)(binding :: reversed)

let recursive fillEnvironment environmentTemp captures index state =
    match captures with
        | [] -> success(environmentTemp)(SemInt)(state)
        | binding :: rest ->
            match lowerBoundVariable(binding)(state) with
                | LoweredCoreValue { state = captureState, error = Some(error) } -> failure(captureState)(error)
                | LoweredCoreValue { state = captureState, temp = captureTemp, error = None } ->
                    let storedState =
                        emit(StoreMemOffset(environmentTemp)(index * 8)(captureTemp))(captureState)
                    in fillEnvironment(environmentTemp)(rest)(index + 1)(storedState)

let recursive captureCount captures =
    match captures with
        | [] -> 0
        | _ :: rest -> 1 + captureCount(rest)

let allocateEnvironment captures stackAllocate state =
    match freshTemp(state) with
        | FreshTemp { state = tempState, temp = environmentTemp } ->
            match captures with
                | [] ->
                    tempState
                    |> emit(LoadConstInt(environmentTemp)(0))
                    |> success(environmentTemp)(SemInt)
                | _ ->
                    let byteCount = 8 * captureCount(captures)
                    in
                        let allocatedState =
                            if stackAllocate
                            then
                                emit(AllocStack(environmentTemp)(byteCount))(tempState)
                            else
                                emit(Alloc(environmentTemp)(byteCount)(false))(tempState)
                        in fillEnvironment(environmentTemp)(captures)(0)(allocatedState)

// Dead-capture pruning. The free-variable analysis decides a lambda's captures before its body is
// lowered, so a capture the lowered body never reads through LoadEnv still gets an environment
// word and a fill. The body is lowered first here, into its own instruction list, so the captures
// its instructions actually read are known before the environment is built at the creation site:
// the survivors are renumbered to a compact 0..k-1 range in the body's LoadEnv indices, and the
// environment is then allocated and filled for the survivors only, so every consumer that derives
// its offsets from the capture list's enumeration order stays consistent without patching. The
// self-referential and mutual-recursion paths never come through here: a self reference rebuilds a
// closure over this same environment with a size fixed before pruning could run, and a group's
// environment is shared and filled once at the group site.
let recursive containsIndex (index: Int) (indices: List(Int)) =
    match indices with
        | [] -> false
        | candidate :: rest ->
            if candidate == index
            then true
            else containsIndex(index)(rest)

let recursive collectLoadEnvIndices instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = LoadEnv(_, index) } :: rest ->
            if containsIndex(index)(acc)
            then collectLoadEnvIndices(rest)(acc)
            else collectLoadEnvIndices(rest)(index :: acc)
        | _ :: rest -> collectLoadEnvIndices(rest)(acc)

let recursive countIndices (indices: List(Int)) =
    match indices with
        | [] -> 0
        | _ :: rest -> 1 + countIndices(rest)

// (survivors in capture order, old index -> new index for each survivor)
let recursive pruneCaptureList captures usedIndices (index: Int) (nextIndex: Int) survivors remap =
    match captures with
        | [] -> (reverse(survivors), reverse(remap))
        | capture :: rest ->
            if containsIndex(index)(usedIndices)
            then pruneCaptureList(rest)(usedIndices)(index + 1)(nextIndex + 1)(capture :: survivors)((index, nextIndex) :: remap)
            else pruneCaptureList(rest)(usedIndices)(index + 1)(nextIndex)(survivors)(remap)

let recursive remappedIndex (index: Int) (remap: List((Int, Int))) =
    match remap with
        | [] -> index
        | (oldIndex, newIndex) :: rest ->
            if oldIndex == index
            then newIndex
            else remappedIndex(index)(rest)

let recursive renumberLoadEnv instructions remap acc =
    match instructions with
        | [] -> reverse(acc)
        | IrInstruction { instruction = LoadEnv(target, index), location = location } :: rest ->
            renumberLoadEnv(rest)(remap)(IrInstruction(instruction = remap
            |> remappedIndex(index)
            |> LoadEnv(target), location = location) :: acc)
        | head :: rest -> renumberLoadEnv(rest)(remap)(head :: acc)

// (surviving captures, the body's instructions with LoadEnv indices renumbered), in the input order
// of both lists; unchanged when every capture is read.
let pruneDeadCaptures captures instructions =
    (let usedIndices = collectLoadEnvIndices(instructions)([])
    in
        if countIndices(usedIndices) >= captureCount(captures)
        then (captures, instructions)
        else
            match pruneCaptureList(captures)(usedIndices)(0)(0)([])([]) with
                | (survivors, remap) -> (survivors, renumberLoadEnv(instructions)(remap)([])))

let recursive capturedScope captures index =
    match captures with
        | [] -> []
        | CoreBinding { name = name, scheme = scheme } :: rest ->
            let binding = CoreBinding(name = name, scheme = scheme, location = CoreEnvironment(index), ownedRead = false)
            in binding :: capturedScope(rest)(index + 1)

let recursive sealOperatorDefaults label pending sealed =
    match pending with
        | [] -> sealed
        | (position, semanticType) :: rest -> sealOperatorDefaults(label)(rest)((Ashes.Internal.deepCopy(label), position, semanticType) :: sealed)

let finishLiftedFunction label origin bodyState =
    match bodyState with
        | CoreLoweringState { reversedInstructions = instructions, functions = functions, nextLocal = localCount, nextTemp = tempCount, pendingOperatorDefaults = pending, sealedOperatorDefaults = sealed } ->
            let function =
                IrFunction(
                    label = label,
                    instructions = reverse(instructions),
                    localCount = localCount,
                    tempCount = tempCount,
                    hasEnvAndArgParams = true,
                    coroutine = None,
                    localNames = [],
                    localTypes = [],
                    origin = Some(origin),
                    lifetimesPlaced = false
                )
            in
                bodyState
                |> (given (current: CoreLoweringState) -> current with functions = append(functions)([function]))
                |> (given (current: CoreLoweringState) -> current with pendingOperatorDefaults = [])
                |> (given (current: CoreLoweringState) -> current with sealedOperatorDefaults = sealOperatorDefaults(label)(pending)(sealed))

let restoreOuterFrame outer bodyState =
    match bodyState with
        | CoreLoweringState { functions = functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId, nextStringId = nextStringId, stringLiterals = stringLiterals, typeSupply = typeSupply, substitution = substitution, sealedOperatorDefaults = sealedOperatorDefaults, runtimeNormalizedArgumentLabels = normalizedLabels } ->
            outer
            |> (given (current: CoreLoweringState) -> current with functions = functions)
            |> (given (current: CoreLoweringState) -> current with runtimeNormalizedArgumentLabels = normalizedLabels)
            |> (given (current: CoreLoweringState) -> current with nextLambdaId = nextLambdaId)
            |> (given (current: CoreLoweringState) -> current with nextLabelId = nextLabelId)
            |> (given (current: CoreLoweringState) -> current with nextStringId = nextStringId)
            |> (given (current: CoreLoweringState) -> current with stringLiterals = stringLiterals)
            |> (given (current: CoreLoweringState) -> current with typeSupply = typeSupply)
            |> (given (current: CoreLoweringState) -> current with substitution = substitution)
            |> (given (current: CoreLoweringState) -> current with sealedOperatorDefaults = sealedOperatorDefaults)
            |> (given (current: CoreLoweringState) -> current with pendingClosureNormalizers = bodyState.pendingClosureNormalizers)

let emitClosure label environmentTemp captureTotal stackAllocate state =
    match freshTemp(state) with
        | FreshTemp { state = tempState, temp = closureTemp } ->
            let byteCount = captureTotal * 8
            in
                let returnsRuntimeManaged = bodyReturnsRuntimeManaged(label)(tempState)
                in
                    let acceptsRuntimeManaged = acceptsRuntimeManagedArgument(label)(tempState)
                    in
                        let closureState =
                            if stackAllocate
                            then
                                emit(MakeClosureStack(closureTemp)(label)(environmentTemp)(byteCount)(returnsRuntimeManaged)(acceptsRuntimeManaged))(tempState)
                            else
                                acceptsRuntimeManaged
                                |> MakeClosure(closureTemp)(label)(environmentTemp)(byteCount)(false)(returnsRuntimeManaged)
                                |> (given (instruction) -> emit(instruction)(tempState))
                        in (closureState, closureTemp)

let prepareLambdaBodyState parameter parameterType captures lambdaId origin state =
    (let functionBindings =
        CoreBinding(
            name = parameter,
            scheme = emptyScheme(parameterType),
            location = CoreLocal(1),
            ownedRead = false
        ) :: capturedScope(captures)(0)
    in
        state
        |> enterFunctionOrigin(origin)
        |> (given (current: CoreLoweringState) -> current with reversedInstructions = [])
        |> (given (current: CoreLoweringState) -> current with bindings = functionBindings)
        |> (given (current: CoreLoweringState) -> current with nextTemp = 0)
        |> (given (current: CoreLoweringState) -> current with nextLocal = 2)
        |> (given (current: CoreLoweringState) -> current with pendingOperatorDefaults = [])
        |> (given (current: CoreLoweringState) -> current with resourceStates = [])
        |> (given (current: CoreLoweringState) -> current with nextLambdaId = lambdaId + 1))

// A capture that a runtime-managed copy of the closure environment re-establishes by copying its
// word: the scalars. Strings, bytes, big integers, lists, tuples, and named types need the copy-out
// and closure-dropper helpers that are not ported yet, so a closure capturing one gets no
// normalizer here.
let scalarCaptureText (semanticType: SemanticType) =
    match semanticType with
        | SemInt -> Some("Int")
        | SemUInt(bits) -> Some("u" + Ashes.Text.fromInt(bits))
        | SemFloat -> Some("Float")
        | SemRune -> Some("Rune")
        | SemBool -> Some("Bool")
        | _ -> None

// `(environment offset, type text)` per capture, in environment order, when every capture is a
// scalar.
let finalCaptureType (defaulted: List(Int)) (state: CoreLoweringState) (captureType: SemanticType) =
    match resolveType(state)(captureType) with
        | SemVariable(id) ->
            if containsInt(id)(defaulted)
            then SemInt
            else SemVariable(id)
        | resolved -> resolved

// `(environment offset, type text)` per capture, in environment order, when every capture is a
// scalar.
let recursive scalarCaptureLayout (types: List(SemanticType)) (index: Int) (defaulted: List(Int)) (state: CoreLoweringState) =
    match types with
        | [] -> Some([])
        | captureType :: rest ->
            match (captureType
            |> finalCaptureType(defaulted)(state)
            |> scalarCaptureText, scalarCaptureLayout(rest)(index + 1)(defaulted)(state)) with
                | (Some(text), Some(layout)) -> Some((index * 8, text) :: layout)
                | _ -> None

let recursive captureTypes (captures: List(CoreBinding)) =
    match captures with
        | [] -> []
        | CoreBinding { scheme = TypeScheme { body = captureType } } :: rest -> captureType :: captureTypes(rest)

let recursive captureLayoutText (layout: List((Int, Str))) =
    match layout with
        | [] -> ""
        | (offset, text) :: rest ->
            match rest with
                | [] -> Ashes.Text.fromInt(offset) + ":" + text
                | _ -> Ashes.Text.fromInt(offset) + ":" + text + ";" + captureLayoutText(rest)

// The normalizer's instructions carry the location of the closure they belong to, the ambient
// span stage 0 synthesizes them under.
let locatedInstruction (location: Maybe(IrSourceLocation)) kind = IrInstruction(instruction = kind, location = location)

// The word copies of the normalizer body: capture `i` is read from the source environment (temp
// 0) at its offset and stored into the target environment (temp 1) at the same offset, on temps
// from 2 upward.
let recursive normalizerCopies (layout: List((Int, Str))) (temp: Int) (location: Maybe(IrSourceLocation)) =
    match layout with
        | [] -> []
        | (offset, _text) :: rest ->
            locatedInstruction(location)(LoadMemOffset(temp)(0)(offset)) :: locatedInstruction(location)(StoreMemOffset(1)(offset)(temp)) :: normalizerCopies(rest)(temp + 1)(location)

let closureNormalizerOrigin (label: Str) (closureLabel: Str) (closureOrigin: IrFunctionOrigin) (layoutText: Str) =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = ClosureEnvironmentNormalizerOrigin,
        sourceOrigin = closureOrigin.sourceOrigin,
        parentGeneratedLabel = Some(closureLabel),
        compilerOwner = None,
        stableDiscriminator = Some(layoutText),
        generationLocation = None
    )

// Stage 0's `lambda$env_normalize` helper for a closure whose captures are all scalars: called
// with the source environment in slot 0 and the target environment in slot 1, it copies every
// capture across and returns 0, the "no dropper" address, since scalar captures own nothing.
let closureNormalizerFunction (closureLabel: Str) (closureOrigin: IrFunctionOrigin) (layout: List((Int, Str))) (location: Maybe(IrSourceLocation)) =
    ((given (labelled) ->
        match labelled with
            | (label, resultTemp) ->
                IrFunction(
                    label = label,
                    instructions = append(locatedInstruction(location)(LoadLocal(0)(0)) :: locatedInstruction(location)(LoadLocal(1)(1)) :: normalizerCopies(layout)(2)(location))([0
                    |> LoadConstInt(resultTemp)
                    |> locatedInstruction(location), locatedInstruction(location)(Return(resultTemp))]),
                    localCount = 2,
                    tempCount = resultTemp + 1,
                    hasEnvAndArgParams = true,
                    coroutine = None,
                    localNames = [],
                    localTypes = [],
                    origin = layout
                    |> captureLayoutText
                    |> closureNormalizerOrigin(label)(closureLabel)(closureOrigin)
                    |> Some,
                    lifetimesPlaced = false
                )))((closureLabel + "$env_normalize", 2 + length(layout)))

// Records a capturing closure for a normalizer decided at program finalization, when the
// substitution and the deferred operator defaults are final; a capture-free closure needs none.
let recordClosureNormalizer (closureLabel: Str) (captures: List(CoreBinding)) (closureOrigin: IrFunctionOrigin) (state: CoreLoweringState) =
    match captures with
        | [] -> state
        | _ -> state with pendingClosureNormalizers = (closureLabel, closureOrigin, captureTypes(captures), currentLocation(state)) :: state.pendingClosureNormalizers

let recursive insertAfterLabel (label: Str) (inserted: IrFunction) (functions: List(IrFunction)) =
    match functions with
        | [] -> [inserted]
        | (IrFunction { label = candidate } as function) :: rest ->
            if candidate == label
            then function :: inserted :: rest
            else function :: insertAfterLabel(label)(inserted)(rest)

// Places each recorded closure's normalizer right after the closure's own function, as stage 0
// synthesizes it when the closure is emitted, for the closures whose captures all resolve to
// scalars.
let recursive insertClosureNormalizers (pending: List((Str, IrFunctionOrigin, List(SemanticType), Maybe(IrSourceLocation)))) (defaulted: List(Int)) (state: CoreLoweringState) (functions: List(IrFunction)) =
    match pending with
        | [] -> functions
        | (closureLabel, closureOrigin, types, location) :: rest ->
            match scalarCaptureLayout(types)(0)(defaulted)(state) with
                | Some(layout) ->
                    functions
                    |> insertAfterLabel(closureLabel)(closureNormalizerFunction(closureLabel)(closureOrigin)(layout)(location))
                    |> insertClosureNormalizers(rest)(defaulted)(state)
                | None -> insertClosureNormalizers(rest)(defaulted)(state)(functions)

let finishClosureResult parameterType bodyType finishedBody closure =
    match closure with
        | (closureState, closureTemp) ->
            let resultType = resolveType(finishedBody)(bodyType)
            in
                success(closureTemp)(SemFunction(parameterType)(resultType)(None))(closureState)

let emitPrunedClosure label origin captures stackAllocate parameterType bodyType finishedBody allocated =
    match allocated with
        | LoweredCoreValue { state = environmentState, error = Some(error) } -> failure(environmentState)(error)
        | LoweredCoreValue { state = environmentState, temp = environmentTemp, error = None } ->
            match emitClosure(label)(environmentTemp)(captureCount(captures))(stackAllocate)(environmentState) with
                | (closureState, closureTemp) -> finishClosureResult(parameterType)(bodyType)(finishedBody)((recordClosureNormalizer(label)(captures)(origin)(closureState), closureTemp))

let finishLambdaBody label origin captures stackAllocate typedOuter parameterType lowered =
    match lowered with
        | LoweredCoreValue { state = failedBody, error = Some(error) } -> failure(failedBody)(error)
        | LoweredCoreValue { state = loweredBody, temp = bodyTemp, semanticType = bodyType, error = None } ->
            let returned = emit(Return(bodyTemp))(loweredBody)
            in
                match pruneDeadCaptures(captures)(returned.reversedInstructions) with
                    | (survivors, prunedInstructions) ->
                        let finishedBody = finishLiftedFunction(label)(origin)((returned with reversedInstructions = prunedInstructions))
                        in
                            finishedBody
                            |> restoreOuterFrame(typedOuter)
                            |> recordBodyRuntimeManaged(label)(isRuntimeTemp(bodyTemp)(loweredBody))
                            |> markCapturedResourcesMoved(survivors)
                            |> allocateEnvironment(survivors)(stackAllocate)
                            |> emitPrunedClosure(label)(origin)(survivors)(stackAllocate)(parameterType)(bodyType)(finishedBody)

// A type annotation (an ADT constructor field's, or — via `lowerLambdaParameterType` below — an
// explicit lambda parameter's) is resolved against exactly the scalar primitives listed here, plus
// (via `parameterTypes`) the enclosing type's own type parameters — not through
// `TypeResolution.ash`'s real `resolveTypeExpression`, which needs a full `TypeEnvironment` this
// single-file pipeline does not build. An annotation outside this list (a function, a resource, a
// capability row) answers `None` — the caller's job to treat that as "can't check this one," not as
// an error, since it is a gap in this resolver, not proof the annotation is invalid.
let recursive lookupTypeParameter (name: Str) (parameterTypes: List((Str, SemanticType))) =
    match parameterTypes with
        | [] -> None
        | (candidateName, semanticType) :: rest ->
            if candidateName == name
            then Some(semanticType)
            else lookupTypeParameter(name)(rest)

let recursive typeExprToSemanticType (typeExpr: TypeExpr) (parameterTypes: List((Str, SemanticType))) =
    match typeExpr with
        | TypeAt(_span, inner) -> typeExprToSemanticType(inner)(parameterTypes)
        | TypeNamed("Int") -> Some(SemInt)
        | TypeNamed("Str") -> Some(SemString)
        | TypeNamed("Bool") -> Some(SemBool)
        | TypeNamed("Float") -> Some(SemFloat)
        | TypeNamed("BigInt") -> Some(SemBigInt)
        | TypeNamed("Rune") -> Some(SemRune)
        | TypeNamed("Bytes") -> Some(SemBytes)
        | TypeUnit ->
            []
            |> SemNamed(0)("Unit")
            |> Some
        | TypeNamed(name) ->
            match lookupTypeParameter(name)(parameterTypes) with
                | Some(parameterType) -> Some(parameterType)
                | None ->
                    []
                    |> SemNamed(0)(name)
                    |> Some
        | TypeApplied("List", element :: []) ->
            match typeExprToSemanticType(element)(parameterTypes) with
                | None -> None
                | Some(elementType) -> Some(SemList(elementType))
        | TypeApplied(name, arguments) ->
            match typeExprListToSemanticTypes(arguments)(parameterTypes) with
                | None -> None
                | Some(argumentTypes) ->
                    argumentTypes
                    |> SemNamed(0)(name)
                    |> Some
        | TypeTuple(elements) ->
            match typeExprListToSemanticTypes(elements)(parameterTypes) with
                | None -> None
                | Some(elementTypes) -> Some(SemTuple(elementTypes))
        | _other -> None
and typeExprListToSemanticTypes (typeExprs: List(TypeExpr)) (parameterTypes: List((Str, SemanticType))) =
    match typeExprs with
        | [] -> Some([])
        | typeExpr :: rest ->
            match typeExprToSemanticType(typeExpr)(parameterTypes) with
                | None -> None
                | Some(semanticType) ->
                    match typeExprListToSemanticTypes(rest)(parameterTypes) with
                        | None -> None
                        | Some(restTypes) -> Some(semanticType :: restTypes)

// Binds a lambda parameter's fresh type variable to its explicit annotation, when one was written
// and this resolver can understand it — `given (x: Bool) -> ...`, `let f (x: Bool) = ...` (the
// parser desugars both into the same `ExprLambda` shape). An unresolvable annotation (a function
// type, a resource, anything `typeExprToSemanticType` answers `None` for) is left unchecked rather
// than rejected, matching this resolver's existing constructor-field contract: an unproven "can't
// check this" is not the same as "this is wrong."
let lowerLambdaParameterType annotation parameterType state =
    match annotation with
        | None -> (state, None)
        | Some(typeExpr) ->
            match typeExprToSemanticType(typeExpr)([]) with
                | None -> (state, None)
                | Some(annotationType) -> bindType(parameterType)(annotationType)(state)

// A let-bound lambda's generated label is remembered under the let's name, so a call through the
// name can consult the function's recorded body placement.
let recordLetLambdaLabel (label: Str) (state: CoreLoweringState) =
    match state.pendingSourceFunction with
        | Some(SourceFunctionOrigin { functionSourceName = name }) -> state with letLambdaLabels = (name, label) :: state.letLambdaLabels
        | None -> state

// Stage 0's `LowerEscapingResult` at a function body: a body that is itself a fresh string
// producer is asked to place its result on the reference-counted heap.
let functionBodyRequest (body: Expr) (state: CoreLoweringState) = emptyConsumerRequest with runtimeString = isRuntimeRcStringProducer(body)(state)

// The copy an entry normalization makes of a borrowed argument, decided from the parameter's
// resolved type the way stage 0's `EmitRuntimeManagedTcoParamCopy` and its deep-copy walk decide
// theirs: a scalar is kept as is, a string/bytes/bigint leaf and a same-arity scalar-field ADT
// are copied out in one piece, a list over copyable heads copies its spine, and a tuple or a
// runtime-managed ADT copies its owned children recursively. A constructor plan carries the
// constructor's tag, its cell size, whether the cell is tagless, and its owned children by field
// index.
type ArgumentCopyPlan =
    | ScalarArgumentCopy
    | LeafArgumentCopy(Int)
    | ListHeadArgumentCopy(ListHeadCopyKind)
    | TupleArgumentCopy(List(ArgumentCopyPlan))
    | ShallowAdtArgumentCopy(Int)
    | ConstructorArgumentCopy((Int, Int, Bool, List((Int, ArgumentCopyPlan))))
    | SwitchArgumentCopy(List((Int, Int, Bool, List((Int, ArgumentCopyPlan)))))

let bigIntCopySizeBytes = -2

// The cell size of one constructor: one word per field, plus the tag word unless tagless.
let adtAllocationSizeBytes (layout: CoreConstructorLayout) =
    match layout with
        | CoreConstructorLayout { tagless = tagless } ->
            if tagless
            then 8 * constructorArity(layout)
            else 8 + 8 * constructorArity(layout)

// The constructor layouts of a named type, in declaration order.
let recursive constructorLayoutsOfType (name: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | layout :: rest ->
            if layoutResultName(layout) == Some(name)
            then layout :: constructorLayoutsOfType(name)(rest)
            else constructorLayoutsOfType(name)(rest)

// The described children of one constructor that own a reference, by field index and type.
let recursive ownedConstructorChildren (constructorName: Str) (children: List(HeapLayoutChild)) =
    match children with
        | [] -> []
        | HeapLayoutChild { dropKind = NoChildDrop } :: rest -> ownedConstructorChildren(constructorName)(rest)
        | HeapLayoutChild { constructorName = Some(owner), fieldIndex = index, childType = childType } :: rest ->
            if owner == constructorName
            then (index, childType) :: ownedConstructorChildren(constructorName)(rest)
            else ownedConstructorChildren(constructorName)(rest)
        | _ :: rest -> ownedConstructorChildren(constructorName)(rest)

// The list-head copy kind of a list argument: scalar heads copy inline, string heads and heads
// that are lists of scalars copy with the spine; any other element type has no spine copy.
let listHeadCopyKindOf (element: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(element) with
        | SemString -> Some(StringListHead)
        | SemList(inner) ->
            if inner
            |> resolveType(state)
            |> canArenaResetLayout
            then Some(InnerListHead)
            else None
        | resolved ->
            if canArenaResetLayout(resolved)
            then Some(InlineListHead)
            else None

let runtimeManagedAdtLayout (facts: HeapLayoutFacts) =
    match facts with
        | HeapLayoutFacts { runtimeRecordAdtSupported = record, runtimeOwnedChildAdtSupported = ownedChild, runtimeTcoOwnedChildAdtSupported = tcoOwnedChild } -> record || ownedChild || tcoOwnedChild

let recursive argumentCopyPlanOf (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> Some(LeafArgumentCopy(-1))
        | SemBytes -> Some(LeafArgumentCopy(-1))
        | SemBigInt -> Some(LeafArgumentCopy(bigIntCopySizeBytes))
        | SemList(element) ->
            match listHeadCopyKindOf(element)(state) with
                | Some(headCopy) -> Some(ListHeadArgumentCopy(headCopy))
                | None -> None
        | SemTuple(elements) ->
            match argumentCopyPlansOf(elements)(state) with
                | Some(plans) -> Some(TupleArgumentCopy(plans))
                | None -> None
        | SemNamed(_symbolId, name, _arguments) as named ->
            match state
            |> coverageEnvironment
            |> classifyHeapLayout(named) with
                | HeapLayoutFacts { structuralCopy = ShallowCopy } ->
                    Some(state
                    |> shallowAdtCopySizeBytes(name)
                    |> ShallowAdtArgumentCopy)
                | HeapLayoutFacts { children = children } as facts ->
                    if runtimeManagedAdtLayout(facts)
                    then
                        adtCopyPlanOf(constructorLayoutsOfType(name)(state.constructorLayouts))(children)(state)
                    else None
        | resolved ->
            if canArenaResetLayout(resolved)
            then Some(ScalarArgumentCopy)
            else None
and argumentCopyPlansOf (types: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> Some([])
        | semanticType :: rest ->
            match (argumentCopyPlanOf(semanticType)(state), argumentCopyPlansOf(rest)(state)) with
                | (Some(plan), Some(plans)) -> Some(plan :: plans)
                | _ -> None
and childCopyPlansOf (children: List((Int, SemanticType))) (state: CoreLoweringState) =
    match children with
        | [] -> Some([])
        | (index, childType) :: rest ->
            match (argumentCopyPlanOf(childType)(state), childCopyPlansOf(rest)(state)) with
                | (Some(plan), Some(plans)) -> Some((index, plan) :: plans)
                | _ -> None
and constructorCopyPlansOf (layouts: List(CoreConstructorLayout)) (children: List(HeapLayoutChild)) (state: CoreLoweringState) =
    match layouts with
        | [] -> Some([])
        | (CoreConstructorLayout { name = constructorName, tag = tag, tagless = tagless } as layout) :: rest ->
            match (childCopyPlansOf(ownedConstructorChildren(constructorName)(children))(state), constructorCopyPlansOf(rest)(children)(state)) with
                | (Some(plans), Some(restPlans)) -> Some((tag, adtAllocationSizeBytes(layout), tagless, plans) :: restPlans)
                | _ -> None
and adtCopyPlanOf (layouts: List(CoreConstructorLayout)) (children: List(HeapLayoutChild)) (state: CoreLoweringState) =
    match constructorCopyPlansOf(layouts)(children)(state) with
        | Some(single :: []) -> Some(ConstructorArgumentCopy(single))
        | Some([]) -> None
        | Some(plans) -> Some(SwitchArgumentCopy(plans))
        | None -> None

// The parameter types an entry normalization turns into an owned runtime-managed value, stage
// 0's `IsRuntimeNormalizableParameterType`: strings, and the ADTs the runtime copies out or
// deep-copies.
let entryNormalizationPlanOf (parameterType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(parameterType) with
        | SemString -> argumentCopyPlanOf(parameterType)(state)
        | SemNamed(_symbolId, _name, _arguments) -> argumentCopyPlanOf(parameterType)(state)
        | _ -> None

let rcNormalizationCopyOut copyTemp sourceTemp sizeBytes state =
    state
    |> emit(CopyOutArena(copyTemp)(sourceTemp)(sizeBytes)(true)(RcNormalization)(None))
    |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)

let rcNormalizationListCopyOut copyTemp sourceTemp headCopy state =
    state
    |> emit(CopyOutList(copyTemp)(sourceTemp)(headCopy)(true)(RcNormalization))
    |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)

let firstSwitchLabel (cases: List(IrSwitchCase)) =
    match cases with
        | IrSwitchCase { label = label } :: _rest -> label
        | [] -> ""

// Stage 0's `EmitRuntimeManagedTcoDeepCopy`: a scalar is returned as is; every other plan copies
// into a fresh temp, the constructor and switch walks allocating their own result temps after it.
let recursive emitArgumentDeepCopy (sourceTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match plan with
        | ScalarArgumentCopy -> (state, sourceTemp)
        | _ ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = resultTemp } ->
                    match plan with
                        | LeafArgumentCopy(sizeBytes) -> (rcNormalizationCopyOut(resultTemp)(sourceTemp)(sizeBytes)(allocated), resultTemp)
                        | ShallowAdtArgumentCopy(sizeBytes) -> (rcNormalizationCopyOut(resultTemp)(sourceTemp)(sizeBytes)(allocated), resultTemp)
                        | ListHeadArgumentCopy(headCopy) -> (rcNormalizationListCopyOut(resultTemp)(sourceTemp)(headCopy)(allocated), resultTemp)
                        | TupleArgumentCopy(elements) ->
                            allocated
                            |> emit(Alloc(resultTemp)(8 * coreListLength(elements))(true))
                            |> emitTupleElementCopies(sourceTemp)(resultTemp)(0)(elements)
                            |> markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)
                            |> (given (copied) -> (copied, resultTemp))
                        | ConstructorArgumentCopy(constructorPlan) -> emitConstructorDeepCopy(sourceTemp)(constructorPlan)(allocated)
                        | SwitchArgumentCopy(constructorPlans) -> emitAdtSwitchDeepCopy(sourceTemp)(constructorPlans)(allocated)
                        | ScalarArgumentCopy -> (allocated, sourceTemp)
and emitTupleElementCopies (sourceTemp: Int) (resultTemp: Int) (index: Int) (elements: List(ArgumentCopyPlan)) (state: CoreLoweringState) =
    match elements with
        | [] -> state
        | element :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = childTemp } ->
                    match allocated
                    |> emit(LoadMemOffset(childTemp)(sourceTemp)(8 * index))
                    |> emitArgumentDeepCopy(childTemp)(element) with
                        | (copied, copiedChild) ->
                            copied
                            |> emit(StoreMemOffset(resultTemp)(8 * index)(copiedChild))
                            |> emitTupleElementCopies(sourceTemp)(resultTemp)(index + 1)(rest)
// Stage 0's `EmitRuntimeManagedTcoConstructorDeepCopy`: the cell is copied out whole, then every
// owned child is read from the source, deep-copied, and stored into the copy.
and emitConstructorDeepCopy (sourceTemp: Int) (constructorPlan: (Int, Int, Bool, List((Int, ArgumentCopyPlan)))) (state: CoreLoweringState) =
    match (constructorPlan, freshTemp(state)) with
        | ((_tag, sizeBytes, tagless, children), FreshTemp { state = allocated, temp = resultTemp }) ->
            allocated
            |> emit(CopyOutArena(resultTemp)(sourceTemp)(sizeBytes)(true)(RcNormalization)(None))
            |> emitConstructorChildCopies(sourceTemp)(resultTemp)(tagless)(children)
            |> markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)
            |> (given (copied) -> (copied, resultTemp))
and emitConstructorChildCopies (sourceTemp: Int) (resultTemp: Int) (tagless: Bool) (children: List((Int, ArgumentCopyPlan))) (state: CoreLoweringState) =
    match children with
        | [] -> state
        | (index, plan) :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = childTemp } ->
                    match allocated
                    |> emit(GetAdtField(childTemp)(sourceTemp)(index)(tagless))
                    |> emitArgumentDeepCopy(childTemp)(plan) with
                        | (copied, copiedChild) ->
                            copied
                            |> emit(SetAdtField(resultTemp)(index)(copiedChild)(tagless))
                            |> emitConstructorChildCopies(sourceTemp)(resultTemp)(tagless)(rest)
// Stage 0's `EmitRuntimeManagedTcoAdtDeepCopy` for a type with several constructors: the tag
// selects the constructor walk, each branch storing its copy into a shared slot.
and emitAdtSwitchDeepCopy (sourceTemp: Int) (constructorPlans: List((Int, Int, Bool, List((Int, ArgumentCopyPlan))))) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = slotState, local = resultSlot } ->
            match freshTemp(slotState) with
                | FreshTemp { state = tagState, temp = tagTemp } ->
                    match tagState
                    |> emit(GetAdtTag(tagTemp)(sourceTemp))
                    |> switchCasesOf(constructorPlans) with
                        | (casesState, cases) ->
                            match freshLabel("rc_normalize_adt_end")(casesState) with
                                | FreshLabel { state = endState, label = endLabel } ->
                                    match endState
                                    |> emit(cases
                                    |> firstSwitchLabel
                                    |> SwitchTag(tagTemp)(cases))
                                    |> emitSwitchBranches(sourceTemp)(resultSlot)(endLabel)(cases)(constructorPlans)
                                    |> emit(Label(endLabel))
                                    |> freshTemp with
                                        | FreshTemp { state = resultState, temp = resultTemp } ->
                                            resultState
                                            |> emit(LoadLocal(resultTemp)(resultSlot))
                                            |> markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)
                                            |> (given (loaded) -> (loaded, resultTemp))
and switchCasesOf (constructorPlans: List((Int, Int, Bool, List((Int, ArgumentCopyPlan))))) (state: CoreLoweringState) =
    match constructorPlans with
        | [] -> (state, [])
        | (tag, _sizeBytes, _tagless, _children) :: rest ->
            match freshLabel("rc_normalize_adt")(state) with
                | FreshLabel { state = labelState, label = label } ->
                    match switchCasesOf(rest)(labelState) with
                        | (casesState, cases) -> (casesState, IrSwitchCase(tag = tag, label = label) :: cases)
and emitSwitchBranches (sourceTemp: Int) (resultSlot: Int) (endLabel: Str) (cases: List(IrSwitchCase)) (constructorPlans: List((Int, Int, Bool, List((Int, ArgumentCopyPlan))))) (state: CoreLoweringState) =
    match (cases, constructorPlans) with
        | (IrSwitchCase { label = label } :: caseRest, constructorPlan :: planRest) ->
            match state
            |> emit(Label(label))
            |> emitConstructorDeepCopy(sourceTemp)(constructorPlan) with
                | (copied, branchTemp) ->
                    copied
                    |> emit(StoreLocal(resultSlot)(branchTemp))
                    |> emit(Jump(endLabel))
                    |> emitSwitchBranches(sourceTemp)(resultSlot)(endLabel)(caseRest)(planRest)
        | _ -> state

// Stage 0's `EmitRuntimeManagedTcoParamCopy`: the copy of a borrowed argument. A string, a
// same-arity scalar-field ADT, and a list copy into the temp allocated here; a tuple and a
// runtime-managed ADT walk their children through the deep copy on temps of its own.
let emitArgumentCopy (sourceTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = normalizedTemp } ->
            match plan with
                | ListHeadArgumentCopy(headCopy) ->
                    (emit(CopyOutList(normalizedTemp)(sourceTemp)(headCopy)(true)(RcNormalization))(allocated), normalizedTemp)
                | TupleArgumentCopy(_elements) -> emitArgumentDeepCopy(sourceTemp)(plan)(allocated)
                | ConstructorArgumentCopy(_constructorPlan) -> emitArgumentDeepCopy(sourceTemp)(plan)(allocated)
                | SwitchArgumentCopy(_constructorPlans) -> emitArgumentDeepCopy(sourceTemp)(plan)(allocated)
                | LeafArgumentCopy(sizeBytes) ->
                    (emit(CopyOutArena(normalizedTemp)(sourceTemp)(sizeBytes)(true)(RcNormalization)(None))(allocated), normalizedTemp)
                | ShallowAdtArgumentCopy(sizeBytes) ->
                    (emit(CopyOutArena(normalizedTemp)(sourceTemp)(sizeBytes)(true)(RcNormalization)(None))(allocated), normalizedTemp)
                | ScalarArgumentCopy -> (allocated, sourceTemp)

// Stage 0's `EmitRuntimeManagedTcoArgumentNormalization`: the hidden ownership flag says whether
// the caller handed over a retained reference; a borrowed argument is copied into an owned value,
// and either lands in the result slot.
let emitArgumentOwnershipNormalization (sourceTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = slotState, local = resultSlot } ->
            match freshTemp(slotState) with
                | FreshTemp { state = ownershipState, temp = ownershipTemp } ->
                    match freshLabel("rc_arg_normalize_copy")(ownershipState) with
                        | FreshLabel { state = copyLabelState, label = copyLabel } ->
                            match freshLabel("rc_arg_normalize_done")(copyLabelState) with
                                | FreshLabel { state = labelState, label = doneLabel } ->
                                    match labelState
                                    |> emit(LoadArgumentOwnership(ownershipTemp))
                                    |> emit(JumpIfFalse(ownershipTemp)(copyLabel))
                                    |> emit(StoreLocal(resultSlot)(sourceTemp))
                                    |> emit(Jump(doneLabel))
                                    |> emit(Label(copyLabel))
                                    |> emitArgumentCopy(sourceTemp)(plan) with
                                        | (copied, copiedTemp) ->
                                            match copied
                                            |> emit(StoreLocal(resultSlot)(copiedTemp))
                                            |> emit(Label(doneLabel))
                                            |> freshTemp with
                                                | FreshTemp { state = resultState, temp = resultTemp } ->
                                                    (emit(LoadLocal(resultTemp)(resultSlot))(resultState), resultTemp)

// The entry block of a function that normalizes its argument: the argument is reloaded from
// slot 1, normalized, and stored back, ahead of the body already lowered.
let emitEntryArgumentNormalization (plan: ArgumentCopyPlan) (label: Str) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { reversedInstructions = bodyInstructions } ->
            match freshTemp((state with reversedInstructions = [])) with
                | FreshTemp { state = allocated, temp = sourceTemp } ->
                    match allocated
                    |> emit(LoadLocal(sourceTemp)(1))
                    |> emitArgumentOwnershipNormalization(sourceTemp)(plan) with
                        | (normalized, normalizedTemp) ->
                            normalized
                            |> emit(StoreLocal(1)(normalizedTemp))
                            |> (given (entry: CoreLoweringState) -> entry with reversedInstructions = append(bodyInstructions)(entry.reversedInstructions))
                            |> recordRuntimeNormalizedArgument(label)

let recursive constructorLayoutNames (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | CoreConstructorLayout { name = name } :: rest -> name :: constructorLayoutNames(rest)

// Stage 0's `LowerLambdaCoreNormalizeAlwaysReturnedParameter`: a function whose parameter always
// reaches its result keeps the argument past the call, so it takes ownership of a runtime-managed
// argument: it advertises that it accepts one and copies a borrowed argument into an owned value
// at entry. Only a string or ADT parameter with a copy plan is normalized; a scalar parameter,
// or one whose type has no plan yet, is left as it is.
let normalizeAlwaysReturnedParameter parameter body label parameterType lowered =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = bodyState } ->
            match entryNormalizationPlanOf(parameterType)(bodyState) with
                | None -> lowered
                | Some(plan) ->
                    if acceptsRuntimeManagedArgument(label)(bodyState) || !resultAlwaysReachesVariable(constructorLayoutNames(bodyState.constructorLayouts))(bodyState.letLambdas)(body)(parameter)
                    then lowered
                    else lowered with state = emitEntryArgumentNormalization(plan)(label)(bodyState)

let lowerLambdaBody parameter body stackAllocate lower lambdaId captures origin fresh =
    match fresh with
        | FreshType { state = typedOuter, semanticType = parameterType } ->
            typedOuter
            |> prepareLambdaBodyState(parameter)(parameterType)(captures)(lambdaId)(origin)
            |> withConsumerRequest(functionBodyRequest(body)(typedOuter))
            |> lower(body)
            |> normalizeAlwaysReturnedParameter(parameter)(body)("lambda_" + Ashes.Text.fromInt(lambdaId))(parameterType)
            |> finishLambdaBody("lambda_" + Ashes.Text.fromInt(lambdaId))(origin)(captures)(stackAllocate)(typedOuter)(parameterType)

let resolvedFunctionType state argumentType resultType =
    FunctionTypeResolution(
        state = state,
        argumentType = argumentType,
        resultType = resultType,
        error = None
    )

let failedFunctionType state error =
    FunctionTypeResolution(
        state = state,
        argumentType = SemNever,
        resultType = SemNever,
        error = Some(error)
    )

let finishFreshFunctionType semanticType fresh =
    match fresh with
        | FreshFunctionType { state = state, semanticType = functionType, parameterType = argumentType, resultType = resultType } ->
            match bindType(semanticType)(functionType)(state) with
                | (unifiedState, None) -> resolvedFunctionType(unifiedState)(argumentType)(resultType)
                | (failedState, Some(error)) -> failedFunctionType(failedState)(error)

let ensureFunctionType semanticType state =
    match resolveType(state)(semanticType) with
        | SemFunction(argumentType, resultType, _row) -> resolvedFunctionType(state)(argumentType)(resultType)
        | SemVariable(_id) ->
            state
            |> freshFunctionType
            |> finishFreshFunctionType(semanticType)
        | other -> failedFunctionType(state)(CoreCallRequiresFunction(other))

// An expected function type pins the parameter type before the body is lowered; its result type
// meets the body's type through the context's own unification afterwards.
let applyExpectedLambdaType parameterType state =
    match expectedTypeOf(state) with
        | None -> (clearConsumerRequest(state), None)
        | Some(expected) ->
            match state
            |> clearConsumerRequest
            |> ensureFunctionType(expected) with
                | FunctionTypeResolution { state = failedState, error = Some(error) } -> (failedState, Some(error))
                | FunctionTypeResolution { state = functionState, argumentType = argumentType, error = None } -> bindType(argumentType)(parameterType)(functionState)

// The lambda's origin is decided against the armed let name before the outer frame forgets it:
// the outer continuation must not hand the same name to a later lambda.
let lowerLambda parameter body annotation stackAllocate lower state =
    match state with
        | CoreLoweringState { bindings = outerBindings, nextLambdaId = lambdaId } ->
            match (capturedBindings(collectFree(body)([parameter])([]))(outerBindings)([]), lambdaOriginFor("lambda_" + Ashes.Text.fromInt(lambdaId))(parameter)(state)) with
                | (captures, origin) ->
                    match freshType(((given (armed: CoreLoweringState) -> armed with pendingSourceFunction = None, pendingStackClosure = false))(recordLetLambdaLabel("lambda_" + Ashes.Text.fromInt(lambdaId))(state))) with
                        | FreshType { state = freshState, semanticType = parameterType } ->
                            match applyExpectedLambdaType(parameterType)(freshState) with
                                | (expectedFailed, Some(error)) -> failure(expectedFailed)(error)
                                | (expectedState, None) ->
                                    match lowerLambdaParameterType(annotation)(parameterType)(expectedState) with
                                        | (checkedState, Some(error)) -> failure(checkedState)(error)
                                        | (checkedState, None) -> lowerLambdaBody(parameter)(body)(stackAllocate)(lower)(lambdaId)(captures)(origin)(FreshType(state = checkedState, semanticType = parameterType))

// Closes a call's arena window after its last application under the scope rule.
let closeCallWindow cursorSlot endSlot lowered =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            state
            |> closeScopeForResult(temp)(semanticType)(cursorSlot)(endSlot)
            |> success(temp)(semanticType)

let finishCoreCall functionTemp argumentTemp resultType binding =
    match binding with
        | (unifiedState, Some(error)) -> failure(unifiedState)(error)
        | (unifiedState, None) ->
            match freshTemp(unifiedState) with
                | FreshTemp { state = targetState, temp = target } ->
                    targetState
                    |> emit(CallClosure(target)(functionTemp)(argumentTemp)(-1))
                    |> success(target)(resolveType(unifiedState)(resultType))

let lowerCoreCallArgument functionTemp expectedArgumentType resultType loweredArgument =
    match loweredArgument with
        | LoweredCoreValue { state = argumentState, error = Some(error) } -> failure(argumentState)(error)
        | LoweredCoreValue { state = argumentState, temp = argumentTemp, semanticType = argumentType, error = None } ->
            argumentState
            |> bindType(expectedArgumentType)(argumentType)
            |> finishCoreCall(functionTemp)(argumentTemp)(resultType)

// An argument is expected to have the callee's parameter type.
let lowerCoreCallTyped argument lower functionTemp resolved =
    match resolved with
        | FunctionTypeResolution { state = typedState, error = Some(error) } -> failure(typedState)(error)
        | FunctionTypeResolution { state = typedState, argumentType = expectedType, resultType = resultType, error = None } ->
            typedState
            |> withOnlyExpectedType(Some(expectedType))
            |> lower(argument)
            |> lowerCoreCallArgument(functionTemp)(expectedType)(resultType)

let lowerCoreCallFunction argument lower loweredFunction =
    match loweredFunction with
        | LoweredCoreValue { state = functionState, error = Some(error) } -> failure(functionState)(error)
        | LoweredCoreValue { state = functionState, temp = functionTemp, semanticType = functionType, error = None } ->
            functionState
            |> ensureFunctionType(functionType)
            |> lowerCoreCallTyped(argument)(lower)(functionTemp)

// Unifies the result a spine of `arity` applications of the callee produces with the type the
// context expects of the call, before any argument is lowered: a callee type that is still a
// variable at some arrow is made a function type on the way.
let recursive preconstrainCallResultType functionType arity expected state =
    if arity == 0
    then bindType(functionType)(expected)(state)
    else
        match ensureFunctionType(functionType)(state) with
            | FunctionTypeResolution { state = failedState, error = Some(_error) } -> (failedState, None)
            | FunctionTypeResolution { state = functionState, resultType = resultType, error = None } -> preconstrainCallResultType(resultType)(arity - 1)(expected)(functionState)

let preconstrainCallResult expected arity loweredCallee =
    match (expected, loweredCallee) with
        | (None, _) -> loweredCallee
        | (_, LoweredCoreValue { error = Some(_error) }) -> loweredCallee
        | (Some(expectedType), LoweredCoreValue { state = state, temp = temp, semanticType = functionType, error = None }) ->
            match preconstrainCallResultType(functionType)(arity)(expectedType)(state) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (constrainedState, None) -> success(temp)(functionType)(constrainedState)

// The callee of one application inside a call spine `f(a)(b)`: a further application is another
// stage of the same spine, an applied lambda literal is lowered as a stack closure, and anything
// else is an ordinary expression. The stages share the window `lowerCall` opened around the
// whole spine; a call inside an argument opens its own. The root callee's result after the
// spine's `arity` applications is constrained to the expected type before the arguments.
let recursive lowerCallSpineCallee expression expected arity lower state =
    match expression with
        | ExprAt(_span, inner) -> lowerCallSpineCallee(inner)(expected)(arity)(lower)(state)
        | ExprCall(function, argument, _isSugar, _layout) -> lowerCallSpineStage(function)(argument)(expected)(arity + 1)(lower)(state)
        | ExprLambda(parameter, body, annotation) ->
            state
            |> lowerLambda(parameter)(body)(annotation)(true)(lower)
            |> preconstrainCallResult(expected)(arity)
        | _ ->
            state
            |> lower(expression)
            |> preconstrainCallResult(expected)(arity)
and lowerCallSpineStage function argument expected arity lower state =
    state
    |> lowerCallSpineCallee(function)(expected)(arity)(lower)
    |> lowerCoreCallFunction(argument)(lower)

// A general call keeps its chain's intermediates in an arena window of its own, saved before the
// callee and arguments are lowered and closed after the last application.
let lowerCall (spine: CoreCallSpine) function argument expected lower state =
    match openArenaBracket(state) with
        | ArenaBracket { bracketState = opened, bracketCursorSlot = cursorSlot, bracketEndSlot = endSlot } ->
            opened
            |> lowerCallSpineStage(function)(argument)(expected)(1)(lower)
            |> markKnownCallResult(spine)
            |> closeCallWindow(cursorSlot)(endSlot)

let failedIfPlan state error =
    CoreIfPlan(
        state = state,
        resultSlot = -1,
        elseLabel = "",
        endLabel = "",
        error = Some(error)
    )

let finishIfPlan conditionTemp elseLabel endLabel fresh =
    match fresh with
        | FreshLocal { state = state, local = resultSlot } ->
            CoreIfPlan(
                state = emit(JumpIfFalse(conditionTemp)(elseLabel))(state),
                resultSlot = resultSlot,
                elseLabel = elseLabel,
                endLabel = endLabel,
                error = None
            )

let prepareIfEndLabel conditionTemp elseLabel fresh =
    match fresh with
        | FreshLabel { state = state, label = endLabel } ->
            state
            |> freshLocal
            |> finishIfPlan(conditionTemp)(elseLabel)(endLabel)

let prepareIfElseLabel conditionTemp fresh =
    match fresh with
        | FreshLabel { state = state, label = elseLabel } ->
            state
            |> freshLabel("endif")
            |> prepareIfEndLabel(conditionTemp)(elseLabel)

let prepareTypedIfPlan conditionTemp typed =
    match typed with
        | (failedState, Some(error)) -> failedIfPlan(failedState)(error)
        | (typedState, None) ->
            typedState
            |> freshLabel("else")
            |> prepareIfElseLabel(conditionTemp)

let prepareIfPlan loweredCondition =
    match loweredCondition with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failedIfPlan(failedState)(error)
        | LoweredCoreValue { state = conditionState, temp = conditionTemp, semanticType = conditionType, error = None } ->
            conditionState
            |> bindType(SemBool)(conditionType)
            |> prepareTypedIfPlan(conditionTemp)

let lowerIfThenBranch thenBranch (request: ConsumerRequest) lower plan =
    match plan with
        | CoreIfPlan { state = failedState, error = Some(error) } ->
            CoreIfThen(
                state = failedState,
                resultSlot = -1,
                endLabel = "",
                thenType = SemNever,
                error = Some(error)
            )
        | CoreIfPlan { state = thenState, resultSlot = resultSlot, elseLabel = elseLabel, endLabel = endLabel, error = None } ->
            match thenState
            |> withConsumerRequest(request)
            |> lower(thenBranch) with
                | LoweredCoreValue { state = failedState, error = Some(error) } ->
                    CoreIfThen(
                        state = failedState,
                        resultSlot = resultSlot,
                        endLabel = endLabel,
                        thenType = SemNever,
                        error = Some(error)
                    )
                | LoweredCoreValue { state = resultState, temp = temp, semanticType = semanticType, error = None } ->
                    CoreIfThen(
                        state = resultState
                        |> emit(StoreLocal(resultSlot)(temp))
                        |> emit(Jump(endLabel))
                        |> emit(Label(elseLabel)),
                        resultSlot = resultSlot,
                        endLabel = endLabel,
                        thenType = semanticType,
                        error = None
                    )

let finishIfElseBranch elseBranch (request: ConsumerRequest) lower loweredThen =
    match loweredThen with
        | CoreIfThen { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | CoreIfThen { state = elseState, resultSlot = resultSlot, endLabel = endLabel, thenType = thenType, error = None } ->
            match lower(elseBranch)(withConsumerRequest((request with expectedType = Some(thenType)))(elseState)) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = resultState, temp = temp, semanticType = elseType, error = None } ->
                    match bindType(thenType)(elseType)(resultState) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (typedState, None) ->
                            match freshTemp(typedState) with
                                | FreshTemp { state = targetState, temp = target } ->
                                    targetState
                                    |> emit(StoreLocal(resultSlot)(temp))
                                    |> emit(Label(endLabel))
                                    |> emit(LoadLocal(target)(resultSlot))
                                    |> success(target)(resolveType(typedState)(thenType))

// The then branch inherits the context's expected type; the else branch is expected to have the
// then branch's type.
let lowerIf condition thenBranch elseBranch lower state =
    state
    |> clearConsumerRequest
    |> lower(condition)
    |> prepareIfPlan
    |> lowerIfThenBranch(thenBranch)(state
    |> consumerRequestOf
    |> branchRequest)(lower)
    |> finishIfElseBranch(elseBranch)(state
    |> consumerRequestOf
    |> branchRequest)(lower)

let patternName pattern =
    match pattern with
        | PatternEmptyList -> "empty list"
        | PatternCons(_, _) -> "list cons"
        | PatternTuple(_) -> "tuple"
        | PatternConstructor(_, _) -> "constructor"
        | PatternRecord(_, _) -> "record"
        | PatternAs(_, _) -> "as"
        | PatternOr(_) -> "or"
        | _ -> "pattern"

let finishPatternComparison valueTemp valueType failLabel comparison loweredConstant =
    match loweredConstant with
        | LoweredCoreValue { state = failedState, error = Some(error) } ->
            LoweredCorePattern(
                state = failedState,
                error = Some(error)
            )
        | LoweredCoreValue { state = constantState, temp = constantTemp, semanticType = constantType, error = None } ->
            match bindType(valueType)(constantType)(constantState) with
                | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                | (typedState, None) ->
                    match freshTemp(typedState) with
                        | FreshTemp { state = compareState, temp = compareTemp } ->
                            LoweredCorePattern(
                                state = compareState
                                |> emit(comparison(compareTemp)(valueTemp)(constantTemp))
                                |> emit(JumpIfFalse(compareTemp)(failLabel)),
                                error = None
                            )

let recursive coreRecordPatterns fields =
    match fields with
        | [] -> []
        | (_fieldName, pattern) :: rest -> pattern :: coreRecordPatterns(rest)

// A bare name the parser wrote as a variable pattern but that names a nullary constructor is
// that constructor's pattern, never a binder.
let isNullaryConstructorPatternName (name: Str) state =
    match constructorLayout(name)(state) with
        | Some(CoreConstructorLayout { scheme = TypeScheme { body = SemFunction(_, _, _) } }) -> false
        | Some(_) -> true
        | None -> false

let recursive prepareCorePatternBindings pending seen state =
    match pending with
        | [] -> state
        | pattern :: rest ->
            match pattern with
                | PatternAt(_span, inner) -> prepareCorePatternBindings(inner :: rest)(seen)(state)
                | PatternVar(name) ->
                    if containsName(name)(seen)
                    then prepareCorePatternBindings(rest)(seen)(state)
                    else
                        if isNullaryConstructorPatternName(name)(state)
                        then prepareCorePatternBindings(rest)(seen)(state)
                        else
                            match freshType(state) with
                                | FreshType { state = typedState, semanticType = semanticType } ->
                                    match freshLocal(typedState) with
                                        | FreshLocal { state = localState, local = local } ->
                                            localState
                                            |> addOwnedBinding(name)(emptyScheme(semanticType))(CoreLocal(local))
                                            |> prepareCorePatternBindings(rest)(name :: seen)
                | PatternCons(head, tail) -> prepareCorePatternBindings(head :: tail :: rest)(seen)(state)
                | PatternTuple(elements) ->
                    prepareCorePatternBindings(append(elements)(rest))(seen)(state)
                | PatternConstructor(_name, elements) ->
                    prepareCorePatternBindings(append(elements)(rest))(seen)(state)
                | PatternRecord(_name, fields) ->
                    prepareCorePatternBindings(append(coreRecordPatterns(fields))(rest))(seen)(state)
                | PatternAs(inner, name) ->
                    if containsName(name)(seen)
                    then prepareCorePatternBindings(inner :: rest)(seen)(state)
                    else
                        match freshType(state) with
                            | FreshType { state = typedState, semanticType = semanticType } ->
                                match freshLocal(typedState) with
                                    | FreshLocal { state = localState, local = local } ->
                                        localState
                                        |> addOwnedBinding(name)(emptyScheme(semanticType))(CoreLocal(local))
                                        |> prepareCorePatternBindings(inner :: rest)(name :: seen)
                | PatternOr(first :: _alternatives) -> prepareCorePatternBindings(first :: rest)(seen)(state)
                | _ -> prepareCorePatternBindings(rest)(seen)(state)

let preparePattern pattern state = prepareCorePatternBindings([pattern])([])(state)

let lowerPatternVariable name valueTemp valueType state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            match lookupBinding(name)(bindings) with
                | Some(CoreBinding { name = _bindingName, scheme = TypeScheme { quantified = _quantified, body = bindingType, constraints = _constraints }, location = CoreLocal(local) }) ->
                    match bindType(bindingType)(valueType)(state) with
                        | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                        | (typedState, None) ->
                            LoweredCorePattern(
                                state = emit(StoreLocal(local)(valueTemp))(typedState),
                                error = None
                            )
                | None ->
                    LoweredCorePattern(
                        state = state,
                        error = Some(UnsupportedCoreLoweringPattern("missing prepared variable " + name))
                    )
                | Some(_binding) ->
                    LoweredCorePattern(
                        state = state,
                        error = Some(UnsupportedCoreLoweringPattern("invalid prepared variable " + name))
                    )

let recursive freshCoreTypes count reversed state =
    if count <= 0
    then FreshCoreTypes(state = state, semanticTypes = reverse(reversed))
    else
        match freshType(state) with
            | FreshType { state = nextState, semanticType = semanticType } ->
                freshCoreTypes(
                    count - 1,
                    semanticType :: reversed,
                    nextState
                )

let finishPatternNonZero valueTemp failLabel zero =
    match zero with
        | LoweredCoreValue { state = state, temp = zeroTemp } ->
            match freshTemp(state) with
                | FreshTemp { state = compareState, temp = compareTemp } ->
                    LoweredCorePattern(
                        state = compareState
                        |> emit(CmpIntNe(compareTemp)(valueTemp)(zeroTemp))
                        |> emit(JumpIfFalse(compareTemp)(failLabel)),
                        error = None
                    )

let requirePatternNonZero valueTemp failLabel state =
    state
    |> lowerConstant(given (target) -> LoadConstInt(target)(0))(SemInt)
    |> finishPatternNonZero(valueTemp)(failLabel)

let finishEmptyListPattern valueTemp valueType failLabel fresh =
    match fresh with
        | FreshType { state = typedState, semanticType = elementType } ->
            match bindType(valueType)(SemList(elementType))(typedState) with
                | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                | (state, None) ->
                    state
                    |> lowerConstant(given (target) -> LoadConstInt(target)(0))(SemInt)
                    |> finishPatternComparison(valueTemp)(SemInt)(failLabel)(CmpIntEq)

let lowerEmptyListPattern valueTemp valueType failLabel state =
    state
    |> freshType
    |> finishEmptyListPattern(valueTemp)(valueType)(failLabel)

let lowerLoadedPattern pattern valueType failLabel lowerPattern loaded =
    match loaded with
        | FreshTemp { state = state, temp = temp } -> lowerPattern(pattern)(temp)(valueType)(failLabel)(state)

let loadTuplePatternField valueTemp index pattern valueType failLabel lowerPattern state =
    state
    |> freshTemp
    |> (given (fresh) ->
        match fresh with
            | FreshTemp { state = loadState, temp = temp } ->
                FreshTemp(
                    state = emit(LoadMemOffset(temp)(valueTemp)(index * 8))(loadState),
                    temp = temp
                ))
    |> lowerLoadedPattern(pattern)(valueType)(failLabel)(lowerPattern)

let recursive lowerTuplePatternFields patterns types valueTemp index failLabel lowerPattern result =
    match (patterns, types, result) with
        | (_patterns, _types, LoweredCorePattern { error = Some(_error) }) -> result
        | ([], [], _) -> result
        | (pattern :: patternRest, semanticType :: typeRest, LoweredCorePattern { state = state }) ->
            state
            |> loadTuplePatternField(valueTemp)(index)(pattern)(semanticType)(failLabel)(lowerPattern)
            |> lowerTuplePatternFields(patternRest)(typeRest)(valueTemp)(index + 1)(failLabel)(lowerPattern)
        | _ -> result

let finishTuplePattern pattern valueTemp valueType failLabel lowerPattern fresh =
    match (pattern, fresh) with
        | (PatternTuple(patterns), FreshCoreTypes { state = state, semanticTypes = types }) ->
            match bindType(valueType)(SemTuple(types))(state) with
                | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                | (typedState, None) ->
                    let lowerFields = lowerTuplePatternFields(patterns)(types)(valueTemp)(0)(failLabel)
                    in lowerFields(lowerPattern)(LoweredCorePattern(state = typedState, error = None))
        | (_pattern, FreshCoreTypes { state = state }) ->
            LoweredCorePattern(
                state = state,
                error = Some(UnsupportedCoreLoweringPattern("tuple"))
            )

let lowerTuplePattern pattern valueTemp valueType failLabel lowerPattern state =
    match pattern with
        | PatternTuple(patterns) ->
            state
            |> freshCoreTypes(coreListLength(patterns))([])
            |> finishTuplePattern(pattern)(valueTemp)(valueType)(failLabel)(lowerPattern)
        | _ -> LoweredCorePattern(state = state, error = Some(UnsupportedCoreLoweringPattern("tuple")))

let finishConsPatternTail tailPattern tailTemp listType failLabel lowerPattern headResult =
    match headResult with
        | LoweredCorePattern { error = Some(_error) } -> headResult
        | LoweredCorePattern { state = state, error = None } ->
            lowerPattern(
                tailPattern,
                tailTemp,
                listType,
                failLabel,
                state
            )

let lowerConsPatternFields headPattern tailPattern valueTemp elementType failLabel lowerPattern checked =
    match checked with
        | LoweredCorePattern { error = Some(_error) } -> checked
        | LoweredCorePattern { state = state, error = None } ->
            match freshTemp(state) with
                | FreshTemp { state = headState, temp = headTemp } ->
                    match freshTemp(headState) with
                        | FreshTemp { state = tailState, temp = tailTemp } ->
                            let loadedState =
                                tailState
                                |> emit(LoadMemOffset(headTemp)(valueTemp)(0))
                                |> emit(LoadMemOffset(tailTemp)(valueTemp)(8))
                            in
                                loadedState
                                |> lowerPattern(headPattern)(headTemp)(elementType)(failLabel)
                                |> finishConsPatternTail(
                                    tailPattern,
                                    tailTemp,
                                    SemList(elementType),
                                    failLabel,
                                    lowerPattern
                                )

let finishConsPattern headPattern tailPattern valueTemp valueType failLabel lowerPattern fresh =
    match fresh with
        | FreshType { state = state, semanticType = elementType } ->
            match bindType(valueType)(SemList(elementType))(state) with
                | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                | (typedState, None) ->
                    typedState
                    |> requirePatternNonZero(valueTemp)(failLabel)
                    |> lowerConsPatternFields(headPattern)(tailPattern)(valueTemp)(elementType)(failLabel)(lowerPattern)

let lowerConsPattern headPattern tailPattern valueTemp valueType failLabel lowerPattern state =
    state
    |> freshType
    |> finishConsPattern(headPattern)(tailPattern)(valueTemp)(valueType)(failLabel)(lowerPattern)

let lowerAdtPatternField valueTemp index tagless pattern semanticType failLabel lowerPattern result =
    match result with
        | LoweredCorePattern { error = Some(_error) } -> result
        | LoweredCorePattern { state = state, error = None } ->
            match freshTemp(state) with
                | FreshTemp { state = loadState, temp = fieldTemp } ->
                    loadState
                    |> emit(GetAdtField(fieldTemp)(valueTemp)(index)(tagless))
                    |> lowerPattern(pattern)(fieldTemp)(semanticType)(failLabel)

let recursive lowerAdtPatternFields patterns types valueTemp index tagless failLabel lowerPattern result =
    match (patterns, types) with
        | ([], []) -> result
        | (pattern :: patternRest, semanticType :: typeRest) ->
            result
            |> lowerAdtPatternField(valueTemp)(index)(tagless)(pattern)(semanticType)(failLabel)(lowerPattern)
            |> lowerAdtPatternFields(patternRest)(typeRest)(valueTemp)(index + 1)(tagless)(failLabel)(lowerPattern)
        | _ -> result

// An ordinary (tagged) constructor value is a heap pointer that may be null; stage 0 guards every
// tag test with `ptr != 0` first (`EmitRequireNonZero`: zero constant, `CmpIntNe`, jump to the
// arm's fail target on false), and this codegen's null-checked tag read depends on it. The zero
// temp is allocated before the comparison temp, matching stage 0's temp order exactly.
let requireNonZeroPattern valueTemp failLabel state =
    match freshTemp(state) with
        | FreshTemp { state = zeroState, temp = zeroTemp } ->
            match freshTemp(zeroState) with
                | FreshTemp { state = compareState, temp = compareTemp } ->
                    compareState
                    |> emit(LoadConstInt(zeroTemp)(0))
                    |> emit(CmpIntNe(compareTemp)(valueTemp)(zeroTemp))
                    |> emit(JumpIfFalse(compareTemp)(failLabel))

// The tag test allocates its temps in stage 0's `EmitRequireTagMatch` order — tag, compare,
// THEN the expected constant — which is the reverse of every other pattern comparison (a literal
// pattern lowers its constant first, then compares). `finishPatternComparison` would allocate the
// compare temp last, so the tag test emits its own three instructions.
let finishTaggedConstructorTag valueTemp tag failLabel state =
    match freshTemp(state) with
        | FreshTemp { state = tagState, temp = tagTemp } ->
            match freshTemp(tagState) with
                | FreshTemp { state = compareState, temp = compareTemp } ->
                    match freshTemp(compareState) with
                        | FreshTemp { state = expectedState, temp = expectedTemp } ->
                            LoweredCorePattern(
                                state = expectedState
                                |> emit(GetAdtTag(tagTemp)(valueTemp))
                                |> emit(LoadConstInt(expectedTemp)(tag))
                                |> emit(CmpIntEq(compareTemp)(tagTemp)(expectedTemp))
                                |> emit(JumpIfFalse(compareTemp)(failLabel)),
                                error = None
                            )

// A tagless cell is always its type's one constructor: there is no tag to test, and no temp is
// allocated, exactly as stage 0's `EmitRequireTagMatch` returns before allocating any.
let finishConstructorTag valueTemp tag tagless failLabel state =
    if tagless
    then LoweredCorePattern(state = state, error = None)
    else finishTaggedConstructorTag(valueTemp)(tag)(failLabel)(state)

let lowerZeroCostPattern patterns parameterTypes valueTemp failLabel lowerPattern state =
    match (patterns, parameterTypes) with
        | (pattern :: [], semanticType :: []) -> lowerPattern(pattern)(valueTemp)(semanticType)(failLabel)(state)
        | _ ->
            LoweredCorePattern(
                state = state,
                error = Some(UnsupportedCoreLoweringPattern("zero-cost constructor arity"))
            )

let finishConstructorPattern patterns valueTemp valueType failLabel lowerPattern shape =
    match shape with
        | CoreConstructorShape { state = state, layout = CoreConstructorLayout { isZeroCost = true }, parameterTypes = parameterTypes, resultType = resultType } ->
            match bindType(valueType)(resultType)(state) with
                | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                | (typedState, None) ->
                    lowerZeroCostPattern(
                        patterns,
                        parameterTypes,
                        valueTemp,
                        failLabel,
                        lowerPattern,
                        typedState
                    )
        | CoreConstructorShape { state = state, layout = CoreConstructorLayout { tag = tag, tagless = tagless }, parameterTypes = parameterTypes, resultType = resultType } ->
            if coreListLength(patterns) != coreListLength(parameterTypes)
            then LoweredCorePattern(state = state, error = Some(UnsupportedCoreLoweringPattern("constructor arity")))
            else
                match bindType(valueType)(resultType)(state) with
                    | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                    | (typedState, None) ->
                        typedState
                        |> requireNonZeroPattern(valueTemp)(failLabel)
                        |> finishConstructorTag(valueTemp)(tag)(tagless)(failLabel)
                        |> lowerAdtPatternFields(patterns)(parameterTypes)(valueTemp)(0)(tagless)(failLabel)(lowerPattern)

let lowerConstructorPattern name patterns valueTemp valueType failLabel lowerPattern state =
    match constructorLayout(name)(state) with
        | None ->
            LoweredCorePattern(
                state = state,
                error = Some(UnsupportedCoreLoweringPattern("unknown constructor " + name))
            )
        | Some(layout) ->
            state
            |> instantiateConstructor(layout)
            |> finishConstructorPattern(patterns)(valueTemp)(valueType)(failLabel)(lowerPattern)

let recursive findPatternField (name: Str) (fieldNames: List(Str)) (fieldTypes: List(SemanticType)) index =
    match (fieldNames, fieldTypes) with
        | (fieldName :: fieldRest, fieldType :: typeRest) ->
            if name == fieldName
            then Some(CorePatternField(index = index, semanticType = fieldType))
            else findPatternField(name)(fieldRest)(typeRest)(index + 1)
        | _ -> None

let recursive lowerRecordPatternFields fields fieldNames fieldTypes valueTemp tagless failLabel lowerPattern result =
    match (fields, result) with
        | ([], _) -> result
        | (_fields, LoweredCorePattern { error = Some(_error) }) -> result
        | ((fieldName, pattern) :: rest, LoweredCorePattern { state = state, error = None }) ->
            match findPatternField(fieldName)(fieldNames)(fieldTypes)(0) with
                | None ->
                    LoweredCorePattern(state = state, error = fieldName
                    |> UnknownCoreRecordField("record pattern")
                    |> Some)
                | Some(CorePatternField { index = index, semanticType = semanticType }) ->
                    result
                    |> lowerAdtPatternField(valueTemp)(index)(tagless)(pattern)(semanticType)(failLabel)(lowerPattern)
                    |> lowerRecordPatternFields(rest)(fieldNames)(fieldTypes)(valueTemp)(tagless)(failLabel)(lowerPattern)

let finishRecordPattern fields valueTemp valueType failLabel lowerPattern shape =
    match shape with
        | CoreConstructorShape { state = state, layout = CoreConstructorLayout { tag = tag, fieldNames = fieldNames, tagless = tagless }, parameterTypes = fieldTypes, resultType = resultType } ->
            match bindType(valueType)(resultType)(state) with
                | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                | (typedState, None) ->
                    typedState
                    |> requireNonZeroPattern(valueTemp)(failLabel)
                    |> finishConstructorTag(valueTemp)(tag)(tagless)(failLabel)
                    |> lowerRecordPatternFields(fields)(fieldNames)(fieldTypes)(valueTemp)(tagless)(failLabel)(lowerPattern)

let lowerRecordPattern name fields valueTemp valueType failLabel lowerPattern state =
    match constructorLayout(name)(state) with
        | None ->
            LoweredCorePattern(
                state = state,
                error = Some(UnsupportedCoreLoweringPattern("unknown record " + name))
            )
        | Some(layout) ->
            state
            |> instantiateConstructor(layout)
            |> finishRecordPattern(fields)(valueTemp)(valueType)(failLabel)(lowerPattern)

let finishAsPattern name valueTemp valueType inner =
    match inner with
        | LoweredCorePattern { error = Some(_error) } -> inner
        | LoweredCorePattern { state = state, error = None } -> lowerPatternVariable(name)(valueTemp)(valueType)(state)

let finishOrAlternative successLabel result =
    match result with
        | LoweredCorePattern { error = Some(_error) } -> result
        | LoweredCorePattern { state = state, error = None } ->
            LoweredCorePattern(
                state = emit(Jump(successLabel))(state),
                error = None
            )

let labelOrAlternative nextLabel lowered =
    match lowered with
        | LoweredCorePattern { state = state, error = error } ->
            LoweredCorePattern(
                state = emit(Label(nextLabel))(state),
                error = error
            )

let recursive lowerOrAlternatives alternatives valueTemp valueType failLabel successLabel lowerPattern result =
    match (alternatives, result) with
        | (_alternatives, LoweredCorePattern { error = Some(_error) }) -> result
        | ([], _) -> result
        | (alternative :: [], LoweredCorePattern { state = state, error = None }) ->
            match lowerPattern(alternative)(valueTemp)(valueType)(failLabel)(state) with
                | LoweredCorePattern { state = finalState, error = error } ->
                    LoweredCorePattern(
                        state = emit(Label(successLabel))(finalState),
                        error = error
                    )
        | (alternative :: rest, LoweredCorePattern { state = state, error = None }) ->
            match freshLabel("pattern_or_next")(state) with
                | FreshLabel { state = labelState, label = nextLabel } ->
                    labelState
                    |> lowerPattern(alternative)(valueTemp)(valueType)(nextLabel)
                    |> finishOrAlternative(successLabel)
                    |> labelOrAlternative(nextLabel)
                    |> lowerOrAlternatives(rest)(valueTemp)(valueType)(failLabel)(successLabel)(lowerPattern)

let lowerOrPattern alternatives valueTemp valueType failLabel lowerPattern state =
    match freshLabel("pattern_or_match")(state) with
        | FreshLabel { state = labelState, label = successLabel } ->
            let lowerAlternatives = lowerOrAlternatives(alternatives)(valueTemp)(valueType)(failLabel)(successLabel)
            in lowerAlternatives(lowerPattern)(LoweredCorePattern(state = labelState, error = None))

let recursive lowerPattern pattern valueTemp valueType failLabel state =
    match pattern with
        | PatternAt(_span, inner) -> lowerPattern(inner)(valueTemp)(valueType)(failLabel)(state)
        | PatternWildcard -> LoweredCorePattern(state = state, error = None)
        | PatternVar(name) ->
            if isNullaryConstructorPatternName(name)(state)
            then lowerConstructorPattern(name)([])(valueTemp)(valueType)(failLabel)(lowerPattern)(state)
            else lowerPatternVariable(name)(valueTemp)(valueType)(state)
        | PatternEmptyList -> lowerEmptyListPattern(valueTemp)(valueType)(failLabel)(state)
        | PatternCons(head, tail) -> lowerConsPattern(head)(tail)(valueTemp)(valueType)(failLabel)(lowerPattern)(state)
        | PatternTuple(_elements) -> lowerTuplePattern(pattern)(valueTemp)(valueType)(failLabel)(lowerPattern)(state)
        | PatternConstructor(name, patterns) ->
            lowerConstructorPattern(
                name,
                patterns,
                valueTemp,
                valueType,
                failLabel,
                lowerPattern,
                state
            )
        | PatternRecord(name, fields) ->
            lowerRecordPattern(
                name,
                fields,
                valueTemp,
                valueType,
                failLabel,
                lowerPattern,
                state
            )
        | PatternAs(inner, name) ->
            state
            |> lowerPattern(inner)(valueTemp)(valueType)(failLabel)
            |> finishAsPattern(name)(valueTemp)(valueType)
        | PatternOr(alternatives) -> lowerOrPattern(alternatives)(valueTemp)(valueType)(failLabel)(lowerPattern)(state)
        | PatternInt(value) ->
            state
            |> lowerConstant(given (target) -> LoadConstInt(target)(value))(SemInt)
            |> finishPatternComparison(valueTemp)(valueType)(failLabel)(CmpIntEq)
        | PatternRune(value) ->
            state
            |> lowerConstant(given (target) -> LoadConstInt(target)(value))(SemRune)
            |> finishPatternComparison(valueTemp)(valueType)(failLabel)(CmpIntEq)
        | PatternBool(value) ->
            state
            |> lowerConstant(given (target) -> LoadConstBool(target)(value))(SemBool)
            |> finishPatternComparison(valueTemp)(valueType)(failLabel)(CmpIntEq)
        | PatternString(value) ->
            state
            |> lowerString(value)
            |> finishPatternComparison(valueTemp)(valueType)(failLabel)(CmpStrEq)
        | unsupported ->
            LoweredCorePattern(
                state = state,
                error = Some(unsupported
                |> patternName
                |> UnsupportedCoreLoweringPattern)
            )

let lowerMatchGuard guard failLabel lower patternResult =
    match (guard, patternResult) with
        | (_guard, LoweredCorePattern { state = failedState, error = Some(error) }) ->
            LoweredCoreValue(
                state = failedState,
                temp = -1,
                semanticType = SemNever,
                error = Some(error)
            )
        | (None, LoweredCorePattern { state = state, error = None }) -> success(-1)(SemNever)(state)
        | (Some(expression), LoweredCorePattern { state = state, error = None }) ->
            match lower(expression)(state) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = guardState, temp = guardTemp, semanticType = guardType, error = None } ->
                    match bindType(SemBool)(guardType)(guardState) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (typedState, None) ->
                            typedState
                            |> emit(JumpIfFalse(guardTemp)(failLabel))
                            |> success(-1)(SemNever)

// The bindings a match arm added over the bindings outside it, most recent first.
let recursive armBindings (count: Int) (bindings: List(CoreBinding)) =
    match bindings with
        | [] -> []
        | binding :: rest ->
            if count <= 0
            then []
            else binding :: armBindings(count - 1)(rest)

// An owned value a match arm's pattern bound, stage 0's `TrackOwnedBindingsInPattern`: a
// resource by its name, slot, and resource type, any other heap-typed binding by its slot and
// owned type name. A binding whose type is still unresolved once the pattern is lowered owns
// nothing.
type ArmOwner =
    | ArmResourceOwner(Str, Int, Str)
    | ArmHeapOwner(Int, Str)

let armOwnerOf (state: CoreLoweringState) (binding: CoreBinding) =
    match binding with
        | CoreBinding { name = name, location = CoreLocal(slot), ownedRead = true, scheme = TypeScheme { body = bindingType } } ->
            bindingType
            |> resolveType(state)
            |> (given (resolved: SemanticType) ->
                match resourceTypeNameOf(resolved)(state) with
                    | Some(typeName) ->
                        typeName
                        |> ArmResourceOwner(name)(slot)
                        |> Some
                    | None ->
                        match ownedTypeNameOf(resolved)(state.constructorLayouts) with
                            | Some(typeName) ->
                                typeName
                                |> ArmHeapOwner(slot)
                                |> Some
                            | None -> None)
        | _ -> None

let recursive armOwnersOf (state: CoreLoweringState) (bindings: List(CoreBinding)) =
    match bindings with
        | [] -> []
        | binding :: rest ->
            match armOwnerOf(state)(binding) with
                | Some(owner) -> owner :: armOwnersOf(state)(rest)
                | None -> armOwnersOf(state)(rest)

// The owned values an arm's pattern bound, in declaration order, read once the pattern is
// lowered and before its guard, when stage 0 tracks them.
let armOwners (outerBindings: List(CoreBinding)) (patternResult: LoweredCorePattern) =
    match patternResult with
        | LoweredCorePattern { state = state } ->
            state.bindings
            |> armBindings(length(state.bindings) - length(outerBindings))
            |> reverse
            |> armOwnersOf(state)

// An arm result that is the pattern-bound resource itself carries it out of the arm.
let armResultIsBinding (name: Str) (body: Expr) =
    match unspanArgument(body) with
        | ExprVar(candidate) -> candidate == name
        | _ -> false

// Whether an owner is still alive at the arm's exit: a heap owner always is, a resource unless
// the arm released or moved it, or its result carries it out.
let armOwnerAlive (body: Expr) (state: CoreLoweringState) owner =
    match owner with
        | ArmHeapOwner(_slot, _typeName) -> true
        | ArmResourceOwner(name, slot, _typeName) ->
            match resourceStateOf(slot)(state) with
                | Some(_kind) -> false
                | None -> !armResultIsBinding(name)(body)

let recursive anyArmOwnerAlive (body: Expr) (state: CoreLoweringState) (owners: List(ArmOwner)) =
    match owners with
        | [] -> false
        | owner :: rest ->
            if armOwnerAlive(body)(state)(owner)
            then true
            else anyArmOwnerAlive(body)(state)(rest)

// Stage 0's scope-exit drops for a match arm, emitted after the arm result is stored and before
// the arm bracket closes: a live resource the pattern bound is closed (or marked moved when the
// arm result carries it out), any other owned binding is released at the scope exit like an
// owned `let` (the placement pass moves the drop to its last use).
let recursive emitArmOwnerReleases (body: Expr) (owners: List(ArmOwner)) (state: CoreLoweringState) =
    match owners with
        | [] -> state
        | ArmHeapOwner(slot, typeName) :: rest ->
            state
            |> emitOwnedLetRelease(typeName)(slot)
            |> emitArmOwnerReleases(body)(rest)
        | ArmResourceOwner(name, slot, typeName) :: rest ->
            if armResultIsBinding(name)(body)
            then
                state
                |> markResourceReleased(slot)(ResourceMoved)
                |> emitArmOwnerReleases(body)(rest)
            else
                state
                |> emitResourceCleanup(typeName)(slot)
                |> emitArmOwnerReleases(body)(rest)

// The closing reset of an arm's bracket, stage 0's `PopOwnershipScope` at the arm exit: a
// surviving or runtime-managed result resets the arena; a heap result of an arm whose pattern
// owned a live value is copied past the reset when it has a copy-out kind, the copy replacing the
// result in the match's result slot; any other heap result leaves the window open.
let closeArmBracket (bracket: ArenaBracket) (hadAliveOwner: Bool) resultSlot resultTemp resultType state =
    if hadAliveOwner
    then
        match closeOwnedScopeForResult(resultTemp)(resultType)(bracket.bracketCursorSlot)(bracket.bracketEndSlot)(state) with
            | (closed, Some(copyTemp)) ->
                emit(StoreLocal(resultSlot)(copyTemp))(closed)
            | (closed, None) -> closed
    else closeScopeForResult(resultTemp)(resultType)(bracket.bracketCursorSlot)(bracket.bracketEndSlot)(state)

let closeArmScope body owners bracket resultSlot resultTemp resultType (state: CoreLoweringState) =
    state
    |> emitArmOwnerReleases(body)(owners)
    |> closeArmBracket(bracket)(anyArmOwnerAlive(body)(state)(owners))(resultSlot)(resultTemp)(resultType)

let finishMatchArm body resultSlot endLabel resultType (request: ConsumerRequest) outerBindings bracket owners lower guarded =
    match guarded with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = bodyState, error = None } ->
            match bodyState
            |> withConsumerRequest(request)
            |> lower(body) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = resultState, temp = temp, semanticType = bodyType, error = None } ->
                    match bindType(resultType)(bodyType)(resultState) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (typedState, None) ->
                            typedState
                            |> emit(StoreLocal(resultSlot)(temp))
                            |> closeArmScope(body)(owners)(bracket)(resultSlot)(temp)(bodyType)
                            |> emit(Jump(endLabel))
                            |> restoreBindings(outerBindings)
                            |> success(temp)(resultType)

// The guard and body of an arm whose pattern is lowered. The guard lowers through `guardLower`
// and the body through `bodyLower`; the capability-operation arms hand in different ones.
let finishPatternArm body guard failLabel resultSlot endLabel resultType (request: ConsumerRequest) outerBindings bracket guardLower bodyLower patternResult =
    patternResult
    |> lowerMatchGuard(guard)(failLabel)(guardLower)
    |> finishMatchArm(body)(resultSlot)(endLabel)(resultType)(request)(outerBindings)(bracket)(armOwners(outerBindings)(patternResult))(bodyLower)

// One arm is bracketed on its own: `SaveArenaState` before the pattern test, and a matching
// restore/reclaim on BOTH exits — the success path (before the jump to the match end) and the
// `cleanupLabel` block a failed pattern or guard lands on, which then jumps to the real fail
// target. Stage 0 emits the cleanup block for every arm, including one whose pattern cannot fail.
let lowerMatchArm pattern body guard lower cleanupLabel (bracket: ArenaBracket) plan =
    match plan with
        | CoreMatchPlan { state = state, valueTemp = valueTemp, valueType = valueType, resultSlot = resultSlot, endLabel = endLabel, resultType = resultType, armRequest = request } ->
            match state with
                | CoreLoweringState { bindings = outerBindings } ->
                    state
                    |> preparePattern(pattern)
                    |> lowerPattern(pattern)(valueTemp)(valueType)(cleanupLabel)
                    |> finishPatternArm(body)(guard)(cleanupLabel)(resultSlot)(endLabel)(resultType)(request)(outerBindings)(bracket)(lower)(lower)

let recastMatchPlan plan lowered =
    match (plan, lowered) with
        | (CoreMatchPlan { valueTemp = valueTemp, valueType = valueType, resultSlot = resultSlot, endLabel = endLabel, noMatchLabel = noMatchLabel, resultType = resultType, armRequest = request }, LoweredCoreValue { state = state, error = error }) ->
            CoreMatchPlan(
                state = state,
                valueTemp = valueTemp,
                valueType = valueType,
                resultSlot = resultSlot,
                endLabel = endLabel,
                noMatchLabel = noMatchLabel,
                resultType = resultType,
                armRequest = request,
                error = error
            )

let matchFailLabel rest noMatchLabel state =
    match rest with
        | [] -> FreshLabel(state = state, label = noMatchLabel)
        | _ -> freshLabel("match_next")(state)

let labelNextMatchArm rest failLabel (plan: CoreMatchPlan) =
    match rest with
        | [] -> plan
        | _ -> plan with state = emit(Label(failLabel))(plan.state)

// The arm's cleanup block, emitted right after its jump to the match end: restore this arm's own
// bracket, then jump on to the real fail target (`match_next_N`, or the no-match label for the
// last arm). Label allocation order matches stage 0's — the next-arm label first, then this arm's
// cleanup label.
let emitMatchArmCleanup cleanupLabel failLabel (bracket: ArenaBracket) (plan: CoreMatchPlan) =
    match plan with
        | CoreMatchPlan { error = Some(_error) } -> plan
        | CoreMatchPlan { state = state } ->
            plan with state = emit(Jump(failLabel))(state
            |> emit(Label(cleanupLabel))
            |> closeArenaBracket(bracket.bracketCursorSlot)(bracket.bracketEndSlot))

// Brackets one arm of a linear chain: the cleanup label is allocated after the arm's fail label,
// the bracket opens before the pattern test, and the cleanup block follows the arm's jump to the
// match end. `lowerArm` lowers the arm against its cleanup label and bracket.
let lowerBracketedMatchArm lowerArm cleanupLabel failLabel (bracket: ArenaBracket) (bracketedPlan: CoreMatchPlan) =
    bracketedPlan
    |> lowerArm(cleanupLabel)(bracket)
    |> recastMatchPlan(bracketedPlan)
    |> emitMatchArmCleanup(cleanupLabel)(failLabel)(bracket)

let bracketMatchArm lowerArm failLabel (plan: CoreMatchPlan) =
    match freshLabel("match_arm_cleanup")(plan.state) with
        | FreshLabel { state = cleanupState, label = cleanupLabel } ->
            match openArenaBracket(cleanupState) with
                | ArenaBracket { bracketState = bracketState } as bracket -> lowerBracketedMatchArm(lowerArm)(cleanupLabel)(failLabel)(bracket)((plan with state = bracketState))

let labelMatchPlan (label: Str) (plan: CoreMatchPlan) = plan with state = emit(Label(label))(plan.state)

let recursive lowerMatchArms cases lower plan =
    match (cases, plan) with
        | (_cases, CoreMatchPlan { error = Some(_error) }) -> plan
        | ([], _) -> plan
        | ((pattern, body, guard) :: rest, CoreMatchPlan { state = state, noMatchLabel = noMatchLabel }) ->
            match matchFailLabel(rest)(noMatchLabel)(state) with
                | FreshLabel { state = failState, label = failLabel } ->
                    (plan with state = failState)
                    |> bracketMatchArm(lowerMatchArm(pattern)(body)(guard)(lower))(failLabel)
                    |> labelNextMatchArm(rest)(failLabel)
                    |> lowerMatchArms(rest)(lower)

let failedMatchPlan state error =
    CoreMatchPlan(
        state = state,
        valueTemp = -1,
        valueType = SemNever,
        resultSlot = -1,
        endLabel = "",
        noMatchLabel = "",
        resultType = SemNever,
        armRequest = emptyConsumerRequest,
        error = Some(error)
    )

let finishPreparedMatch valueTemp valueType resultType resultSlot endLabel fresh =
    match fresh with
        | FreshLabel { state = state, label = noMatchLabel } ->
            CoreMatchPlan(
                state = state,
                valueTemp = valueTemp,
                valueType = valueType,
                resultSlot = resultSlot,
                endLabel = endLabel,
                noMatchLabel = noMatchLabel,
                resultType = resultType,
                armRequest = emptyConsumerRequest,
                error = None
            )

let withPlanArmRequest (request: ConsumerRequest) (plan: CoreMatchPlan) = plan with armRequest = request

let prepareMatchEndLabel valueTemp valueType resultType resultSlot fresh =
    match fresh with
        | FreshLabel { state = state, label = endLabel } ->
            state
            |> freshLabel("match_none")
            |> finishPreparedMatch(valueTemp)(valueType)(resultType)(resultSlot)(endLabel)

let prepareMatchResultSlot valueTemp valueType resultType fresh =
    match fresh with
        | FreshLocal { state = state, local = resultSlot } ->
            state
            |> freshLabel("match_end")
            |> prepareMatchEndLabel(valueTemp)(valueType)(resultType)(resultSlot)

let prepareMatchResultType valueTemp valueType fresh =
    match fresh with
        | FreshType { state = state, semanticType = resultType } ->
            state
            |> freshLocal
            |> prepareMatchResultSlot(valueTemp)(valueType)(resultType)

let prepareMatchPlan loweredValue =
    match loweredValue with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failedMatchPlan(failedState)(error)
        | LoweredCoreValue { state = valueState, temp = valueTemp, semanticType = valueType, error = None } ->
            valueState
            |> freshType
            |> prepareMatchResultType(valueTemp)(valueType)

let finishMatchPlan plan =
    match plan with
        | CoreMatchPlan { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | CoreMatchPlan { state = state, resultSlot = resultSlot, endLabel = endLabel, noMatchLabel = noMatchLabel, resultType = resultType, error = None } ->
            match freshTemp(state) with
                | FreshTemp { state = defaultState, temp = defaultTemp } ->
                    match freshTemp(defaultState) with
                        | FreshTemp { state = resultState, temp = resultTemp } ->
                            resultState
                            |> emit(Label(noMatchLabel))
                            |> emit(LoadConstInt(defaultTemp)(0))
                            |> emit(StoreLocal(resultSlot)(defaultTemp))
                            |> emit(Label(endLabel))
                            |> emit(LoadLocal(resultTemp)(resultSlot))
                            |> success(resultTemp)(resolveType(resultState)(resultType))

// Tag-group match dispatch. Arms whose patterns are constructors of one ADT are grouped by their
// outer constructor tag; one GetAdtTag/SwitchTag then dispatches to the group, a trivial single
// case binds its payload directly (the switch already proved the tag), and any other group tests
// its cases linearly in their original order, scoped to that group. A group's last case failing
// its own sub-pattern test does not mean nothing matches: the trailing wildcard/variable arm, when
// one exists, still covers it, so every group falls through to that default arm's label and only
// without a default to the match's no-match path. Guards, zero-cost constructors, a second ADT,
// or a catch-all anywhere but last decline to the linear lowering; so does an all-trivial match of
// at most four arms, where linear tag tests cost no more than a switch.
let recursive unwrapPatternAt pattern =
    match pattern with
        | PatternAt(_span, inner) -> unwrapPatternAt(inner)
        | other -> other

let isCatchAllPattern state pattern =
    match unwrapPatternAt(pattern) with
        | PatternWildcard -> true
        | PatternVar(name) -> !isNullaryConstructorPatternName(name)(state)
        | _ -> false

let recursive allCatchAllPatterns state patterns =
    match patterns with
        | [] -> true
        | pattern :: rest ->
            if isCatchAllPattern(state)(pattern)
            then allCatchAllPatterns(state)(rest)
            else false

let classifyConstructorCase (name: Str) patterns state =
    match constructorLayout(name)(state) with
        | None -> CaseReject
        | Some(CoreConstructorLayout { isZeroCost = true }) -> CaseReject
        | Some(CoreConstructorLayout { tag = tag, scheme = TypeScheme { body = body } }) ->
            match schemeResultName(body) with
                | Some(adtName) ->
                    patterns
                    |> allCatchAllPatterns(state)
                    |> CaseConstructor(name)(tag)(adtName)
                | None -> CaseReject

let classifyMatchCase (matchCase: (Pattern, Expr, Maybe(Expr))) state =
    match matchCase with
        | (_pattern, _body, Some(_guard)) -> CaseReject
        | (pattern, _body, None) ->
            match unwrapPatternAt(pattern) with
                | PatternWildcard -> CaseDefault
                | PatternVar(name) ->
                    if isNullaryConstructorPatternName(name)(state)
                    then classifyConstructorCase(name)([])(state)
                    else CaseDefault
                | PatternConstructor(name, patterns) -> classifyConstructorCase(name)(patterns)(state)
                | _ -> CaseReject

let recursive addCaseToTagGroups (groups: List(CoreTagGroup)) (tag: Int) (name: Str) (index: Int) (trivial: Bool) =
    match groups with
        | [] -> [CoreTagGroup(tag = tag, constructorName = name, caseIndices = [index], trivialSingleCase = trivial)]
        | group :: rest ->
            if group.tag == tag
            then (group with caseIndices = append(group.caseIndices)([index]), trivialSingleCase = false) :: rest
            else group :: addCaseToTagGroups(rest)(tag)(name)(index)(trivial)

let recursive anyGroupNeedsLinearFallback (groups: List(CoreTagGroup)) =
    match groups with
        | [] -> false
        | group :: rest ->
            if group.trivialSingleCase
            then anyGroupNeedsLinearFallback(rest)
            else true

let recursive countGroups (groups: List(CoreTagGroup)) (acc: Int) =
    match groups with
        | [] -> acc
        | _ :: rest -> countGroups(rest)(acc + 1)

// Some((groups, default case index)) when the match dispatches through a tag switch.
let recursive planTagGroups cases (index: Int) state (adtName: Maybe(Str)) (groups: List(CoreTagGroup)) =
    match cases with
        | [] ->
            if anyGroupNeedsLinearFallback(groups)
            then Some((groups, None))
            else
                if countGroups(groups)(0) > 4
                then Some((groups, None))
                else None
        | matchCase :: rest ->
            match classifyMatchCase(matchCase)(state) with
                | CaseReject -> None
                | CaseDefault ->
                    match rest with
                        | [] ->
                            match groups with
                                | [] -> None
                                | _ ->
                                    if anyGroupNeedsLinearFallback(groups)
                                    then Some((groups, Some(index)))
                                    else
                                        if countGroups(groups)(0) > 4
                                        then Some((groups, Some(index)))
                                        else None
                        | _ -> None
                | CaseConstructor(name, tag, caseAdt, trivial) ->
                    match adtName with
                        | Some(seen) ->
                            if seen == caseAdt
                            then
                                trivial
                                |> addCaseToTagGroups(groups)(tag)(name)(index)
                                |> planTagGroups(rest)(index + 1)(state)(adtName)
                            else None
                        | None ->
                            trivial
                            |> addCaseToTagGroups(groups)(tag)(name)(index)
                            |> planTagGroups(rest)(index + 1)(state)(Some(caseAdt))

let recursive nthMatchCase cases (index: Int) =
    match cases with
        | [] -> None
        | matchCase :: rest ->
            if index == 0
            then Some(matchCase)
            else nthMatchCase(rest)(index - 1)

// The tag is already proven by the switch: bind the payload fields without re-testing it.
let finishKnownTagConstructorPattern patterns valueTemp valueType failLabel lowerPattern shape =
    match shape with
        | CoreConstructorShape { layout = CoreConstructorLayout { isZeroCost = true } } -> finishConstructorPattern(patterns)(valueTemp)(valueType)(failLabel)(lowerPattern)(shape)
        | CoreConstructorShape { state = state, layout = CoreConstructorLayout { tagless = tagless }, parameterTypes = parameterTypes, resultType = resultType } ->
            if coreListLength(patterns) != coreListLength(parameterTypes)
            then LoweredCorePattern(state = state, error = Some(UnsupportedCoreLoweringPattern("constructor arity")))
            else
                match bindType(valueType)(resultType)(state) with
                    | (failedState, Some(error)) -> LoweredCorePattern(state = failedState, error = Some(error))
                    | (typedState, None) -> lowerAdtPatternFields(patterns)(parameterTypes)(valueTemp)(0)(tagless)(failLabel)(lowerPattern)(LoweredCorePattern(state = typedState, error = None))

let recursive lowerKnownTagPattern pattern valueTemp valueType failLabel state =
    match unwrapPatternAt(pattern) with
        | PatternVar(name) ->
            if isNullaryConstructorPatternName(name)(state)
            then
                lowerKnownTagPattern(PatternConstructor(name)([]))(valueTemp)(valueType)(failLabel)(state)
            else lowerPattern(pattern)(valueTemp)(valueType)(failLabel)(state)
        | PatternConstructor(name, patterns) ->
            match constructorLayout(name)(state) with
                | None -> LoweredCorePattern(state = state, error = Some(UnsupportedCoreLoweringPattern("unknown constructor " + name)))
                | Some(layout) ->
                    state
                    |> instantiateConstructor(layout)
                    |> finishKnownTagConstructorPattern(patterns)(valueTemp)(valueType)(failLabel)(lowerPattern)
        | _ -> lowerPattern(pattern)(valueTemp)(valueType)(failLabel)(state)

// A trivial single-case group arm is bracketed on its success path only: the switch already
// proved the tag and the catch-all sub-patterns cannot fail, so stage 0 emits no cleanup block.
let lowerKnownTagMatchArm pattern body guard failLabel lower (plan: CoreMatchPlan) =
    match (plan.state, openArenaBracket(plan.state)) with
        | (CoreLoweringState { bindings = outerBindings }, ArenaBracket { bracketState = bracketState } as bracket) ->
            bracketState
            |> preparePattern(pattern)
            |> lowerKnownTagPattern(pattern)(plan.valueTemp)(plan.valueType)(failLabel)
            |> finishPatternArm(body)(guard)(failLabel)(plan.resultSlot)(plan.endLabel)(plan.resultType)(plan.armRequest)(outerBindings)(bracket)(lower)(lower)

let groupCaseFailLabel rest (groupFailLabel: Str) state =
    match rest with
        | [] -> FreshLabel(state = state, label = groupFailLabel)
        | _ -> freshLabel("match_group_next")(state)

// The group's cases in their original order, each bracketed like a linear arm; the last one
// fails to the group's fail target.
let recursive lowerTagGroupCasesLinearly cases (indices: List(Int)) (groupFailLabel: Str) lower (plan: CoreMatchPlan) =
    match (indices, plan) with
        | (_indices, CoreMatchPlan { error = Some(_error) }) -> plan
        | ([], _) -> plan
        | (index :: rest, _) ->
            match nthMatchCase(cases)(index) with
                | None -> plan
                | Some((pattern, body, guard)) ->
                    match groupCaseFailLabel(rest)(groupFailLabel)(plan.state) with
                        | FreshLabel { state = failState, label = failLabel } ->
                            (plan with state = failState)
                            |> bracketMatchArm(lowerMatchArm(pattern)(body)(guard)(lower))(failLabel)
                            |> labelNextMatchArm(rest)(failLabel)
                            |> lowerTagGroupCasesLinearly(cases)(rest)(groupFailLabel)(lower)

let lowerTagGroup cases (group: CoreTagGroup) (groupLabel: Str) (groupFailLabel: Str) lower (plan: CoreMatchPlan) =
    match plan with
        | CoreMatchPlan { error = Some(_error) } -> plan
        | _ ->
            let labeled = plan with state = emit(Label(groupLabel))(plan.state)
            in
                if group.trivialSingleCase
                then
                    match group.caseIndices with
                        | index :: _ ->
                            match nthMatchCase(cases)(index) with
                                | Some((pattern, body, guard)) ->
                                    labeled
                                    |> lowerKnownTagMatchArm(pattern)(body)(guard)(groupFailLabel)(lower)
                                    |> recastMatchPlan(labeled)
                                | None -> labeled
                        | [] -> labeled
                else lowerTagGroupCasesLinearly(cases)(group.caseIndices)(groupFailLabel)(lower)(labeled)

let recursive lowerTagGroups cases (groups: List(CoreTagGroup)) (labels: List(Str)) (groupFailLabel: Str) lower (plan: CoreMatchPlan) =
    match (groups, labels) with
        | (group :: groupRest, label :: labelRest) ->
            plan
            |> lowerTagGroup(cases)(group)(label)(groupFailLabel)(lower)
            |> lowerTagGroups(cases)(groupRest)(labelRest)(groupFailLabel)(lower)
        | _ -> plan

let recursive freshGroupLabels (groups: List(CoreTagGroup)) state (reversedLabels: List(Str)) (reversedCases: List(IrSwitchCase)) =
    match groups with
        | [] -> (state, reverse(reversedLabels), reverse(reversedCases))
        | group :: rest ->
            match freshLabel("match_group")(state) with
                | FreshLabel { state = labelState, label = label } -> freshGroupLabels(rest)(labelState)(label :: reversedLabels)(IrSwitchCase(tag = group.tag, label = label) :: reversedCases)

let lowerDefaultTagGroupArm cases (defaultIndex: Maybe(Int)) (defaultLabel: Str) lower (plan: CoreMatchPlan) =
    match (defaultIndex, plan) with
        | (_index, CoreMatchPlan { error = Some(_error) }) -> plan
        | (None, _) -> plan
        | (Some(index), _) ->
            match nthMatchCase(cases)(index) with
                | None -> plan
                | Some((pattern, body, guard)) ->
                    plan
                    |> labelMatchPlan(defaultLabel)
                    |> bracketMatchArm(lowerMatchArm(pattern)(body)(guard)(lower))(plan.noMatchLabel)

// A single-constructor (tagless) scrutinee has exactly one group and nothing to switch on:
// control falls straight into that group's label, and no tag temp is allocated. Its per-case
// sub-pattern tests still run inside the group, and can still fall through to the default arm.
let isSoleTaglessTagGroup (groups: List(CoreTagGroup)) state =
    match groups with
        | CoreTagGroup { constructorName = name } :: [] ->
            match constructorLayout(name)(state) with
                | Some(CoreConstructorLayout { tagless = tagless }) -> tagless
                | None -> false
        | _ -> false

let emitTagGroupSwitch valueTemp switchCases defaultLabel (soleTagless: Bool) state =
    if soleTagless
    then state
    else
        match freshTemp(state) with
            | FreshTemp { state = tagState, temp = tagTemp } ->
                tagState
                |> emit(GetAdtTag(tagTemp)(valueTemp))
                |> emit(SwitchTag(tagTemp)(switchCases)(defaultLabel))

let lowerMatchArmsViaTagGroups cases (groups: List(CoreTagGroup)) (defaultIndex: Maybe(Int)) lower (plan: CoreMatchPlan) =
    match freshGroupLabels(groups)(plan.state)([])([]) with
        | (labelState, groupLabels, switchCases) ->
            let defaultLabelled =
                match defaultIndex with
                    | Some(_) -> freshLabel("match_group_default")(labelState)
                    | None -> FreshLabel(state = labelState, label = plan.noMatchLabel)
            in
                match defaultLabelled with
                    | FreshLabel { state = defaultState, label = defaultLabel } ->
                        let switched =
                            emitTagGroupSwitch(plan.valueTemp)(switchCases)(defaultLabel)(isSoleTaglessTagGroup(groups)(defaultState))(defaultState)
                        in
                            (plan with state = switched)
                            |> lowerTagGroups(cases)(groups)(groupLabels)(defaultLabel)(lower)
                            |> lowerDefaultTagGroupArm(cases)(defaultIndex)(defaultLabel)(lower)

// Dead-arm trimming. A trailing run of arms after a prefix that already matches every value is
// unreachable and is dropped before any arm is lowered. The verdict is only trusted for pattern
// shapes whose coverage reduces to a constructor set, a bool pair, or the two list shapes: a
// catch-all, a bool literal, the empty list, or a cons/tuple/constructor whose every child is a
// catch-all. Anything else (a literal, a record sub-pattern, a nested constructor) stops the
// prefix from growing, since a per-field coverage engine is a deliberate under-approximation of
// what is missing - right for a diagnostic, wrong as a proof that an arm can never run.
let recursive trimCatchAllChildren state patterns =
    match patterns with
        | [] -> true
        | pattern :: rest ->
            if isCatchAllPattern(state)(pattern)
            then trimCatchAllChildren(state)(rest)
            else false

let recursive isExactCoveragePattern state pattern =
    match unwrapPatternAt(pattern) with
        | PatternWildcard -> true
        | PatternVar(_name) -> true
        | PatternBool(_value) -> true
        | PatternEmptyList -> true
        | PatternCons(head, tail) -> trimCatchAllChildren(state)([head, tail])
        | PatternTuple(elements) -> trimCatchAllChildren(state)(elements)
        | PatternConstructor(_name, patterns) -> trimCatchAllChildren(state)(patterns)
        | PatternAs(inner, _name) -> isExactCoveragePattern(state)(inner)
        | PatternOr(alternatives) -> trimExactAlternatives(state)(alternatives)
        | _ -> false
and trimExactAlternatives state alternatives =
    match alternatives with
        | [] -> true
        | alternative :: rest ->
            if isExactCoveragePattern(state)(alternative)
            then trimExactAlternatives(state)(rest)
            else false

// The constructor a pattern names, when it is a constructor pattern (a bare nullary name included).
let trimConstructorName state pattern =
    match unwrapPatternAt(pattern) with
        | PatternConstructor(name, _patterns) ->
            match constructorLayout(name)(state) with
                | Some(_layout) -> Some(name)
                | None -> None
        | PatternVar(name) ->
            if isNullaryConstructorPatternName(name)(state)
            then Some(name)
            else None
        | _ -> None

let trimAdtOfConstructor state (name: Str) =
    match constructorLayout(name)(state) with
        | Some(CoreConstructorLayout { scheme = TypeScheme { body = body } }) -> schemeResultName(body)
        | None -> None

let recursive trimAdtConstructorNames (layouts: List(CoreConstructorLayout)) (adtName: Str) acc =
    match layouts with
        | [] -> reverse(acc)
        | CoreConstructorLayout { name = name, scheme = TypeScheme { body = body } } :: rest ->
            match schemeResultName(body) with
                | Some(candidate) ->
                    if candidate == adtName
                    then trimAdtConstructorNames(rest)(adtName)(name :: acc)
                    else trimAdtConstructorNames(rest)(adtName)(acc)
                | None -> trimAdtConstructorNames(rest)(adtName)(acc)

let stateConstructorLayouts state =
    match state with
        | CoreLoweringState { constructorLayouts = layouts } -> layouts

let recursive trimSeenConstructors state patterns acc =
    match patterns with
        | [] -> acc
        | pattern :: rest ->
            match trimConstructorName(state)(pattern) with
                | Some(name) -> trimSeenConstructors(state)(rest)(name :: acc)
                | None -> trimSeenConstructors(state)(rest)(acc)

let recursive trimAllSeen (names: List(Str)) (seen: List(Str)) =
    match names with
        | [] -> true
        | name :: rest ->
            if containsName(name)(seen)
            then trimAllSeen(rest)(seen)
            else false

let recursive trimAnyCatchAll state patterns =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            if isCatchAllPattern(state)(pattern)
            then true
            else trimAnyCatchAll(state)(rest)

let recursive trimHasBool patterns (wanted: Bool) =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            match unwrapPatternAt(pattern) with
                | PatternBool(value) ->
                    if value == wanted
                    then true
                    else trimHasBool(rest)(wanted)
                | _ -> trimHasBool(rest)(wanted)

let recursive trimHasEmptyList patterns =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            match unwrapPatternAt(pattern) with
                | PatternEmptyList -> true
                | _ -> trimHasEmptyList(rest)

let recursive trimHasCons patterns =
    match patterns with
        | [] -> false
        | pattern :: rest ->
            match unwrapPatternAt(pattern) with
                | PatternCons(_head, _tail) -> true
                | _ -> trimHasCons(rest)

let recursive trimFirstConstructor state patterns =
    match patterns with
        | [] -> None
        | pattern :: rest ->
            match trimConstructorName(state)(pattern) with
                | Some(name) -> Some(name)
                | None -> trimFirstConstructor(state)(rest)

// Whether a prefix of exact-coverage patterns matches every value of its domain.
let trimPrefixCoversAll state patterns =
    if trimAnyCatchAll(state)(patterns)
    then true
    else
        match trimFirstConstructor(state)(patterns) with
            | Some(name) ->
                match trimAdtOfConstructor(state)(name) with
                    | Some(adtName) ->
                        match trimAdtConstructorNames(stateConstructorLayouts(state))(adtName)([]) with
                            | [] -> false
                            | names ->
                                []
                                |> trimSeenConstructors(state)(patterns)
                                |> trimAllSeen(names)
                    | None -> false
            | None ->
                if trimHasBool(patterns)(true)
                then trimHasBool(patterns)(false)
                else
                    if trimHasEmptyList(patterns)
                    then trimHasCons(patterns)
                    else false

let recursive trimTake cases (count: Int) acc =
    match cases with
        | [] -> reverse(acc)
        | matchCase :: rest ->
            if count == 0
            then reverse(acc)
            else trimTake(rest)(count - 1)(matchCase :: acc)

// Grows the prefix one guard-free exact-coverage arm at a time; the first prefix that covers
// every value ends the match there.
let recursive trimProvablyUnreachableTrailingCasesFrom state cases remaining (prefixPatterns: List(Pattern)) (prefixLength: Int) =
    match remaining with
        | [] -> cases
        | (pattern, _body, guard) :: rest ->
            match rest with
                | [] -> cases
                | _ ->
                    match guard with
                        | Some(_guard) -> cases
                        | None ->
                            if isExactCoveragePattern(state)(pattern)
                            then
                                let patterns = append(prefixPatterns)([pattern])
                                in
                                    if trimPrefixCoversAll(state)(patterns)
                                    then trimTake(cases)(prefixLength + 1)([])
                                    else trimProvablyUnreachableTrailingCasesFrom(state)(cases)(rest)(patterns)(prefixLength + 1)
                            else cases

let trimProvablyUnreachableTrailingCases state cases = trimProvablyUnreachableTrailingCasesFrom(state)(cases)(cases)([])(0)

let lowerMatchArmsDispatch allCases lower (plan: CoreMatchPlan) =
    match plan with
        | CoreMatchPlan { error = Some(_error) } -> plan
        | _ ->
            let cases = trimProvablyUnreachableTrailingCases(plan.state)(allCases)
            in
                match planTagGroups(cases)(0)(plan.state)(None)([]) with
                    | Some((groups, defaultIndex)) -> lowerMatchArmsViaTagGroups(cases)(groups)(defaultIndex)(lower)(plan)
                    | None -> lowerMatchArms(cases)(lower)(plan)

let coverageErrorMessage inferenceError =
    match inferenceError with
        | NonExhaustiveMatch(message) -> message
        | UnreachableMatchArm(message) -> message
        | other -> Ashes.Trait.Show.show(other)

let checkCoreMatchCoverage cases (plan: CoreMatchPlan) =
    match plan with
        | CoreMatchPlan { error = Some(_error) } -> plan
        | CoreMatchPlan { state = state, valueType = valueType } ->
            match state
            |> coverageEnvironment
            |> matchCoverageError(cases)(resolveType(state)(valueType)) with
                | None -> plan
                | Some(inferenceError) ->
                    plan with error = Some(inferenceError
                    |> coverageErrorMessage
                    |> CoreMatchCoverageError)

let lowerMatch value cases lower state =
    state
    |> clearConsumerRequest
    |> lower(value)
    |> prepareMatchPlan
    |> withPlanArmRequest(state
    |> consumerRequestOf
    |> branchRequest)
    |> checkCoreMatchCoverage(cases)
    |> lowerMatchArmsDispatch(cases)(lower)
    |> finishMatchPlan

let recursive lambdaParts expression =
    match expression with
        | ExprAt(_span, inner) -> lambdaParts(inner)
        | ExprLambda(parameter, body, _annotation) -> Some((parameter, body))
        | _ -> None

let prepareRecursiveBodyState parameter parameterType captures selfBindings origin state =
    (let functionBindings =
        CoreBinding(
            name = parameter,
            scheme = emptyScheme(parameterType),
            location = CoreLocal(1),
            ownedRead = false
        ) :: append(selfBindings)(capturedScope(captures)(0))
    in
        state
        |> enterFunctionOrigin(origin)
        |> (given (current: CoreLoweringState) -> current with reversedInstructions = [])
        |> (given (current: CoreLoweringState) -> current with bindings = functionBindings)
        |> (given (current: CoreLoweringState) -> current with nextTemp = 0)
        |> (given (current: CoreLoweringState) -> current with pendingOperatorDefaults = [])
        |> (given (current: CoreLoweringState) -> current with resourceStates = [])
        |> (given (current: CoreLoweringState) -> current with nextLocal = 2))

let finishRecursiveLambdaBody prepared origin captures environmentTemp typedOuter lowered =
    match (prepared, lowered) with
        | (_prepared, LoweredCoreValue { state = failedState, error = Some(error) }) -> failure(failedState)(error)
        | (PreparedCoreRecursiveBinding { label = label, semanticType = semanticType, resultType = resultType }, LoweredCoreValue { state = bodyState, temp = bodyTemp, semanticType = bodyType, error = None }) ->
            match bindType(resultType)(bodyType)(bodyState) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (typedBody, None) ->
                    let finishedBody =
                        typedBody
                        |> emit(Return(bodyTemp))
                        |> finishLiftedFunction(label)(origin)
                    in
                        let restored = restoreOuterFrame(typedOuter)(finishedBody)
                        in
                            match emitClosure(label)(environmentTemp)(captureCount(captures))(false)(restored) with
                                | (closureState, closureTemp) ->
                                    success(closureTemp)(resolveType(finishedBody)(semanticType))(closureState)

let lowerPreparedRecursiveLambda prepared selfBindings captures environmentTemp lower state =
    match prepared with
        | PreparedCoreRecursiveBinding { name = name, label = label, parameter = parameter, body = body, parameterType = parameterType } ->
            state
            |> sourceFunctionOrigin(label)(sourceFunctionOriginFor(name)(state))
            |> (given (origin) ->
                state
                |> prepareRecursiveBodyState(parameter)(parameterType)(captures)(selfBindings)(origin)
                |> withConsumerRequest(functionBodyRequest(body)(state))
                |> lower(body)
                |> finishRecursiveLambdaBody(prepared)(origin)(captures)(environmentTemp)(state))

let preparedSelfBinding environmentSize prepared =
    match prepared with
        | PreparedCoreRecursiveBinding { name = name, label = label, semanticType = semanticType } ->
            CoreBinding(
                name = name,
                scheme = emptyScheme(semanticType),
                location = CoreSelf(label)(environmentSize),
                ownedRead = false
            )

let recursive preparedSelfBindings environmentSize members =
    match members with
        | [] -> []
        | member :: rest -> preparedSelfBinding(environmentSize)(member) :: preparedSelfBindings(environmentSize)(rest)

let recursive recursiveBindingNames bindings reversed =
    match bindings with
        | [] -> reverse(reversed)
        | (name, _value) :: rest -> recursiveBindingNames(rest)(name :: reversed)

let recursive collectRecursiveGroupFree bindings groupNames free =
    match bindings with
        | [] -> free
        | (_name, value) :: rest ->
            free
            |> collectFree(value)(groupNames)
            |> collectRecursiveGroupFree(rest)(groupNames)

let finishPreparedRecursiveMember name parameter body lambdaId slot fresh =
    match fresh with
        | FreshFunctionType { state = state, semanticType = semanticType, parameterType = parameterType, resultType = resultType } ->
            PreparedCoreRecursiveMemberResult(
                state = withNextLambdaId(lambdaId + 1)(state),
                member = Some(PreparedCoreRecursiveBinding(
                    name = name,
                    parameter = parameter,
                    body = body,
                    label = "recgroup_" + Ashes.Text.fromInt(lambdaId) + "_" + name,
                    slot = slot,
                    semanticType = semanticType,
                    parameterType = parameterType,
                    resultType = resultType
                )),
                error = None
            )

let allocatePreparedRecursiveMember name parameter body lambdaId fresh =
    match fresh with
        | FreshLocal { state = state, local = slot } ->
            state
            |> freshFunctionType
            |> finishPreparedRecursiveMember(name)(parameter)(body)(lambdaId)(slot)

let prepareRecursiveMember name value state =
    match (lambdaParts(value), state) with
        | (None, _) ->
            PreparedCoreRecursiveMemberResult(
                state = state,
                member = None,
                error = Some(CoreRecursiveBindingRequiresFunction(name))
            )
        | (Some((parameter, body)), CoreLoweringState { nextLambdaId = lambdaId }) ->
            state
            |> freshLocal
            |> allocatePreparedRecursiveMember(name)(parameter)(body)(lambdaId)

let recursive prepareRecursiveGroup bindings state reversed =
    match bindings with
        | [] -> PreparedCoreRecursiveGroup(state = state, members = reverse(reversed), error = None)
        | (name, value) :: rest ->
            match prepareRecursiveMember(name)(value)(state) with
                | PreparedCoreRecursiveMemberResult { state = failedState, error = Some(error) } ->
                    PreparedCoreRecursiveGroup(
                        state = failedState,
                        members = reverse(reversed),
                        error = Some(error)
                    )
                | PreparedCoreRecursiveMemberResult { state = nextState, member = Some(member), error = None } ->
                    prepareRecursiveGroup(
                        rest,
                        nextState,
                        member :: reversed
                    )

let recursive lowerPreparedRecursiveMembers members selfBindings captures environmentTemp lower state =
    match members with
        | [] -> success(-1)(SemNever)(state)
        | (PreparedCoreRecursiveBinding { slot = slot, semanticType = semanticType } as member) :: rest ->
            match lowerPreparedRecursiveLambda(member)(selfBindings)(captures)(environmentTemp)(lower)(state) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = closureState, temp = closureTemp, semanticType = closureType, error = None } ->
                    match bindType(semanticType)(closureType)(closureState) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (typedState, None) ->
                            typedState
                            |> emit(StoreLocal(slot)(closureTemp))
                            |> lowerPreparedRecursiveMembers(rest)(selfBindings)(captures)(environmentTemp)(lower)

let recursive addRecursiveGroupContinuationBindings members outerBindings state =
    match members with
        | [] -> state
        | PreparedCoreRecursiveBinding { name = name, slot = slot, semanticType = semanticType } :: rest ->
            let scheme =
                generalize(pendingOperatorScheme(state) :: bindingSchemes(outerBindings))(resolveType(state)(semanticType))([])
            in
                state
                |> addBinding(name)(scheme)(CoreLocal(slot))
                |> addRecursiveGroupContinuationBindings(rest)(outerBindings)

let finishRecursiveGroupContinuation members outerBindings body (request: ConsumerRequest) lower loweredMembers =
    match loweredMembers with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = groupState, error = None } ->
            let continuationState = addRecursiveGroupContinuationBindings(members)(outerBindings)(groupState)
            in
                match continuationState
                |> withConsumerRequest(request)
                |> lower(body) with
                    | LoweredCoreValue { state = resultState, temp = temp, semanticType = semanticType, error = error } ->
                        LoweredCoreValue(
                            state = restoreBindings(outerBindings)(resultState),
                            temp = temp,
                            semanticType = semanticType,
                            error = error
                        )

// Splits the single `lower` continuation lowerPreparedRecursiveGroup uses into two: memberLower
// lowers each recursive member's own body, continuationLower lowers what follows the group. A
// whole-program driver needs the two to differ (member bodies always go through the ordinary
// expression lowerer; the continuation is "the rest of the top-level items", which isn't an Expr
// at all), so the plain single-`lower` form below is now a same-lowerer convenience wrapper.
let lowerPreparedRecursiveGroupWith bindings body memberLower continuationLower outerBindings prepared =
    match prepared with
        | PreparedCoreRecursiveGroup { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | PreparedCoreRecursiveGroup { state = preparedState, members = members, error = None } ->
            let groupNames = recursiveBindingNames(bindings)([])
            in
                let captures =
                    []
                    |> collectRecursiveGroupFree(bindings)(groupNames)
                    |> (given (names) -> capturedBindings(names)(outerBindings)([]))
                in
                    match preparedState
                    |> clearConsumerRequest
                    |> allocateEnvironment(captures)(false) with
                        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                        | LoweredCoreValue { state = environmentState, temp = environmentTemp, error = None } ->
                            let selfBindings =
                                preparedSelfBindings(captureCount(captures))(members)
                            in
                                environmentState
                                |> lowerPreparedRecursiveMembers(
                                    members,
                                    selfBindings,
                                    captures,
                                    environmentTemp,
                                    memberLower
                                )
                                |> finishRecursiveGroupContinuation(members)(outerBindings)(body)(consumerRequestOf(preparedState))(continuationLower)

let lowerPreparedRecursiveGroup bindings body lower outerBindings prepared = lowerPreparedRecursiveGroupWith(bindings)(body)(lower)(lower)(outerBindings)(prepared)

let lowerRecursiveGroup bindings body lower state =
    match state with
        | CoreLoweringState { bindings = outerBindings } ->
            []
            |> prepareRecursiveGroup(bindings)(state)
            |> lowerPreparedRecursiveGroup(bindings)(body)(lower)(outerBindings)

let relabelSingleRecursive lambdaId prepared =
    match prepared with
        | PreparedCoreRecursiveGroup { state = state, members = member :: [], error = None } ->
            PreparedCoreRecursiveGroup(
                state = state,
                members = [(member with label = "lambda_" + Ashes.Text.fromInt(lambdaId))],
                error = None
            )
        | _ -> prepared

let lowerLetRecursive name value body lower state =
    match state with
        | CoreLoweringState { bindings = outerBindings, nextLambdaId = lambdaId } ->
            []
            |> prepareRecursiveGroup([(name, value)])(state)
            |> relabelSingleRecursive(lambdaId)
            |> lowerPreparedRecursiveGroup([(name, value)])(body)(lower)(outerBindings)

let recursive emitTupleFields baseTemp index temps state =
    match temps with
        | [] -> state
        | temp :: rest ->
            state
            |> emit(StoreMemOffset(baseTemp)(index * 8)(temp))
            |> emitTupleFields(baseTemp)(index + 1)(rest)

let finishTupleLowering lowered =
    match lowered with
        | LoweredCoreValues { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValues { state = state, temps = temps, semanticTypes = semanticTypes, error = None } ->
            match freshTemp(state) with
                | FreshTemp { state = allocatedState, temp = tupleTemp } ->
                    allocatedState
                    |> emit(Alloc(tupleTemp)(coreListLength(temps) * 8)(false))
                    |> emitTupleFields(tupleTemp)(0)(temps)
                    |> success(tupleTemp)(SemTuple(semanticTypes))

// A resource stored into a tuple, list, cons cell, or constructor moves into it.
let lowerTuple elements lower state =
    state
    |> markResourceArgumentsMoved(elements)
    |> lowerCoreValues(elements)(lower)
    |> finishTupleLowering

let allocateListCell headTemp tailTemp elementType state =
    match freshTemp(state) with
        | FreshTemp { state = allocatedState, temp = cellTemp } ->
            allocatedState
            |> emit(Alloc(cellTemp)(16)(false))
            |> emit(StoreMemOffset(cellTemp)(0)(headTemp))
            |> emit(StoreMemOffset(cellTemp)(8)(tailTemp))
            |> success(cellTemp)(elementType
            |> resolveType(allocatedState)
            |> SemList)

let finishCons head tail =
    match (head, tail) with
        | (LoweredCoreValue { state = failedState, error = Some(error) }, _tail) -> failure(failedState)(error)
        | (_head, LoweredCoreValue { state = failedState, error = Some(error) }) -> failure(failedState)(error)
        | (LoweredCoreValue { temp = headTemp, semanticType = headType }, LoweredCoreValue { state = tailState, temp = tailTemp, semanticType = tailType }) ->
            match bindType(SemList(headType))(tailType)(tailState) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (typedState, None) -> allocateListCell(headTemp)(tailTemp)(headType)(typedState)

let finishConsTail lower tailExpression head =
    match head with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = headState, semanticType = headType } ->
            headState
            |> withOnlyExpectedType(Some(SemList(headType)))
            |> lower(tailExpression)
            |> finishCons(head)

// The element type an expected list type asks of a list literal's elements or a cons head.
let expectedListElementType state =
    match expectedTypeOf(state) with
        | None -> None
        | Some(expected) ->
            match resolveType(state)(expected) with
                | SemList(elementType) -> Some(elementType)
                | _ -> None

let lowerCons head tail lower state =
    state
    |> markResourceArgumentsMoved([head, tail])
    |> withOnlyExpectedType(expectedListElementType(state))
    |> lower(head)
    |> finishConsTail(lower)(tail)

let emptyList state =
    match freshType(state) with
        | FreshType { state = typedState, semanticType = elementType } ->
            lowerConstant(given (target) -> LoadConstInt(target)(0))(SemList(elementType))(typedState)

let recursive lowerListElements elements elementType tailTemp lower state =
    match elements with
        | [] ->
            success(tailTemp)(elementType
            |> resolveType(state)
            |> SemList)(state)
        | expression :: rest ->
            match state
            |> withOnlyExpectedType(Some(elementType))
            |> lower(expression) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = valueState, temp = headTemp, semanticType = headType, error = None } ->
                    match bindType(elementType)(headType)(valueState) with
                        | (failedState, Some(error)) -> failure(failedState)(error)
                        | (typedState, None) ->
                            match allocateListCell(headTemp)(tailTemp)(elementType)(typedState) with
                                | LoweredCoreValue { state = cellState, temp = cellTemp, error = None } ->
                                    lowerListElements(
                                        rest,
                                        elementType,
                                        cellTemp,
                                        lower,
                                        cellState
                                    )
                                | failed -> failed

let finishListLiteral elements lower empty =
    match empty with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = emptyState, temp = emptyTemp, semanticType = SemList(elementType), error = None } ->
            lowerListElements(reverse(elements))(elementType)(emptyTemp)(lower)(emptyState)
        | LoweredCoreValue { state = failedState } ->
            failure(
                failedState,
                UnsupportedCoreLoweringExpression("invalid empty list type")
            )

let expectedOrFreshEmptyList state =
    match expectedListElementType(state) with
        | Some(elementType) ->
            state
            |> clearConsumerRequest
            |> lowerConstant(given (target) -> LoadConstInt(target)(0))(SemList(elementType))
        | None ->
            state
            |> clearConsumerRequest
            |> emptyList

let lowerListLiteral elements lower state =
    state
    |> markResourceArgumentsMoved(elements)
    |> expectedOrFreshEmptyList
    |> finishListLiteral(elements)(lower)

let recursive emitAdtFields baseTemp index tagless temps state =
    match temps with
        | [] -> state
        | temp :: rest ->
            state
            |> emit(SetAdtField(baseTemp)(index)(temp)(tagless))
            |> emitAdtFields(baseTemp)(index + 1)(tagless)(rest)

let finishConstructorAllocation layout resultType runtimeManaged lowered =
    match (layout, lowered) with
        | (_layout, LoweredCoreValues { state = failedState, error = Some(error) }) -> failure(failedState)(error)
        | (CoreConstructorLayout { isZeroCost = true }, LoweredCoreValues { state = state, temps = temp :: [], error = None }) ->
            success(temp)(resolveType(state)(resultType))(state)
        | (CoreConstructorLayout { name = name, isZeroCost = true }, LoweredCoreValues { state = state, temps = temps, error = None }) ->
            temps
            |> coreListLength
            |> CoreConstructorArityMismatch(name)(1)
            |> failure(state)
        | (CoreConstructorLayout { tag = tag, tagless = tagless }, LoweredCoreValues { state = state, temps = temps, error = None }) ->
            match freshTemp(state) with
                | FreshTemp { state = allocatedState, temp = resultTemp } ->
                    let fieldCount = coreListLength(temps)
                    in
                        // `runtimeManaged` is the consumer's placement request carried by the
                        // constructor shape (see `instantiateConstructor`); without one the cell is
                        // arena-placed, and any `RcDrop` the consumer pairs with an RC cell is its own.
                        allocatedState
                        |> emit(AllocAdt(resultTemp)(tag)(fieldCount)(runtimeManaged)(tagless))
                        |> emitAdtFields(resultTemp)(0)(tagless)(temps)
                        |> success(resultTemp)(resolveType(allocatedState)(resultType))

let finishConstructorArguments arguments lower shape =
    match shape with
        | CoreConstructorShape { state = state, layout = layout, parameterTypes = parameterTypes, resultType = resultType, constructorRuntimeManaged = runtimeManaged } ->
            let expectedArity = coreListLength(parameterTypes)
            in
                let actualArity = coreListLength(arguments)
                in
                    if expectedArity != actualArity
                    then
                        match layout with
                            | CoreConstructorLayout { name = name } ->
                                actualArity
                                |> CoreConstructorArityMismatch(name)(expectedArity)
                                |> failure(state)
                    else
                        match lowerCoreValues(arguments)(lower)(state) with
                            | LoweredCoreValues { state = failedState, error = Some(error) } -> failure(failedState)(error)
                            | LoweredCoreValues { state = valuesState, semanticTypes = actualTypes, error = None } as lowered ->
                                match bindCoreValueTypes(parameterTypes)(actualTypes)(valuesState) with
                                    | (failedState, Some(error)) -> failure(failedState)(error)
                                    | (typedState, None) ->
                                        let typedValues = lowered with state = typedState
                                        in finishConstructorAllocation(layout)(resultType)(runtimeManaged)(typedValues)

let lowerConstructor layout arguments lower state =
    state
    |> markResourceArgumentsMoved(arguments)
    |> instantiateConstructor(layout)
    |> finishConstructorArguments(arguments)(lower)

let recursive resolveCoreTypes state semanticTypes =
    match semanticTypes with
        | [] -> []
        | semanticType :: rest -> resolveType(state)(semanticType) :: resolveCoreTypes(state)(rest)

let recursive emitCoreInstructions instructions state =
    match instructions with
        | [] -> state
        | instruction :: rest ->
            state
            |> emit(instruction)
            |> emitCoreInstructions(rest)

let finishBuiltinUnit resultType lower state =
    match constructorLayout("Unit")(state) with
        | None -> failure(state)(UnknownLoweringBinding("Unit"))
        | Some(layout) ->
            match lowerConstructor(layout)([])(lower)(state) with
                | LoweredCoreValue { state = unitState, temp = temp, error = None } ->
                    success(temp)(resolveType(unitState)(resultType))(unitState)
                | failed -> failed

let finishBuiltinResult resultType lower result emittedState =
    match result with
        | CoreBuiltinTemp(temp) ->
            success(temp)(resolveType(emittedState)(resultType))(emittedState)
        | CoreBuiltinNever(temp) -> success(temp)(SemNever)(emittedState)
        | CoreBuiltinUnit -> finishBuiltinUnit(resultType)(lower)(emittedState)

// A consumer that keeps nothing of its string argument (`print`, `write`, `writeLine`,
// `writeError`, `Text.byteLength`) releases a newly produced reference-counted one right after
// the use, stage 0's `ReleaseConsumedOwnedOperand` at those sites.
let consumesStringOperand (kind: CoreBuiltinKind) =
    match kind with
        | CorePrint -> true
        | CoreWrite -> true
        | CoreWriteLine -> true
        | CoreWriteError(_newline) -> true
        | CoreTextByteLength -> true
        | _ -> false

let consumedBuiltinOperand (kind: CoreBuiltinKind) (temps: List(Int)) =
    match (consumesStringOperand(kind), temps) with
        | (true, operand :: []) -> Some(operand)
        | _ -> None

let releaseConsumedBuiltinOperand (consumedOperand: Maybe(Int)) (state: CoreLoweringState) =
    match consumedOperand with
        | Some(operand) -> releaseConsumedOperand(operand)(state)
        | None -> state

// A fresh-string builtin that honored the runtime request produced a reference-counted string.
let markFreshStringResult (kind: CoreBuiltinKind) (runtimeManaged: Bool) (lowered: LoweredCoreValue) =
    if runtimeManaged && isFreshStringBuiltinKind(kind)
    then markLoweredRuntimeTemp(lowered)
    else lowered

let finishBuiltinEmission resultType lower consumedOperand state emission =
    match emission with
        | CoreBuiltinEmission { error = Some(error) } -> failure(state)(UnsupportedCoreBuiltinLowering(error))
        | CoreBuiltinEmission { instructions = instructions, nextTemp = nextTemp, result = result, error = None } ->
            state
            |> withNextTemp(nextTemp)
            |> emitCoreInstructions(instructions)
            |> releaseConsumedBuiltinOperand(consumedOperand)
            |> finishBuiltinResult(resultType)(lower)(result)

let emitBuiltin layout resultType lower runtimeManaged lowered =
    match (layout, lowered) with
        | (_, LoweredCoreValues { state = failedState, error = Some(error) }) -> failure(failedState)(error)
        | (CoreBuiltinLayout { kind = kind }, LoweredCoreValues { state = state, temps = temps, semanticTypes = semanticTypes, error = None }) ->
            match state with
                | CoreLoweringState { nextTemp = nextTemp } ->
                    semanticTypes
                    |> resolveCoreTypes(state)
                    |> emitCoreBuiltin(kind)(runtimeManaged)(nextTemp)(temps)
                    |> finishBuiltinEmission(resultType)(lower)(consumedBuiltinOperand(kind)(temps))(state)
                    |> markFreshStringResult(kind)(runtimeManaged)

let emitTypedBuiltin layout resultType lower runtimeManaged (lowered: LoweredCoreValues) typedState =
    emitBuiltin(
        layout,
        resultType,
        lower,
        runtimeManaged,
        lowered with state = typedState
    )

// Stage 0's LowerTextFromInt accepts an `Int` OR a `u8`/`u16`/`u32` argument through an ad-hoc
// rule rather than unification (every uN is stored widened, so the digit phases read the word
// directly). Mirror it by letting a matching small-unsigned actual stand in for the scheme's
// `Int` parameter before the types are bound.
let acceptBuiltinArgumentWidths kind expected actual state =
    match kind with
        | CoreTextFromInt ->
            match (expected, actual) with
                | (SemInt :: expectedTail, actualHead :: _actualTail) ->
                    match resolveType(state)(actualHead) with
                        | SemUInt(bits) ->
                            if bits <= 32
                            then actualHead :: expectedTail
                            else expected
                        | _ -> expected
                | _ -> expected
        | CoreTextToHex ->
            match (expected, actual) with
                | (SemInt :: expectedTail, actualHead :: _actualTail) ->
                    match resolveType(state)(actualHead) with
                        | SemUInt(_bits) -> actualHead :: expectedTail
                        | _ -> expected
                | _ -> expected
        | CoreUIntToInt ->
            match (expected, actual) with
                | (SemUInt(8) :: expectedTail, actualHead :: _actualTail) ->
                    match resolveType(state)(actualHead) with
                        | SemUInt(_bits) -> actualHead :: expectedTail
                        | _ -> expected
                | _ -> expected
        | _ -> expected

let finishBuiltinArity arguments lower runtimeManaged shape expectedArity actualArity =
    match shape with
        | CoreBuiltinShape { state = state, layout = layout, parameterTypes = parameterTypes, resultType = resultType } ->
            if actualArity != expectedArity
            then
                match layout with
                    | CoreBuiltinLayout { moduleName = moduleName, memberName = memberName } ->
                        actualArity
                        |> CoreBuiltinArityMismatch(
                            moduleName,
                            memberName,
                            expectedArity
                        )
                        |> failure(state)
            else
                match state
                |> clearConsumerRequest
                |> lowerCoreValues(arguments)(lower) with
                    | LoweredCoreValues { state = failedState, error = Some(error) } -> failure(failedState)(error)
                    | LoweredCoreValues { state = valuesState, semanticTypes = actualTypes, error = None } as lowered ->
                        let boundParameterTypes =
                            match layout with
                                | CoreBuiltinLayout { kind = builtinKind } -> acceptBuiltinArgumentWidths(builtinKind)(parameterTypes)(actualTypes)(valuesState)
                        in
                            match bindCoreValueTypes(boundParameterTypes)(actualTypes)(valuesState) with
                                | (failedState, Some(error)) -> failure(failedState)(error)
                                | (typedState, None) -> emitTypedBuiltin(layout)(resultType)(lower)(runtimeManaged)(lowered)(typedState)

let finishBuiltinArguments arguments lower runtimeManaged shape =
    match shape with
        | CoreBuiltinShape { parameterTypes = parameterTypes } ->
            finishBuiltinArity(
                arguments,
                lower,
                runtimeManaged,
                shape,
                coreListLength(parameterTypes),
                coreListLength(arguments)
            )

// A builtin reading a resource argument is checked for use after its release before anything is
// lowered; a closing builtin releases the binding once the call is lowered.
let builtinResourceArgument (arguments: List(Expr)) =
    match arguments with
        | first :: _rest -> Some(first)
        | [] -> None

let checkBuiltinResourceUse (kind: CoreBuiltinKind) (arguments: List(Expr)) (state: CoreLoweringState) =
    match (builtinResourceRole(kind), builtinResourceArgument(arguments)) with
        | (BuiltinUsesResource, Some(argument)) -> checkResourceArgumentUse(argument)(state)
        | (BuiltinClosesResource, Some(argument)) -> checkResourceArgumentUse(argument)(state)
        | _ -> None

let releaseClosedBuiltinArgument (kind: CoreBuiltinKind) (arguments: List(Expr)) (lowered: LoweredCoreValue) =
    match (builtinResourceRole(kind), builtinResourceArgument(arguments), lowered) with
        | (BuiltinClosesResource, Some(argument), LoweredCoreValue { state = state, error = None }) -> lowered with state = markResourceArgumentClosed(argument)(state)
        | _ -> lowered

let lowerBuiltin (layout: CoreBuiltinLayout) arguments lower state =
    match checkBuiltinResourceUse(layout.kind)(arguments)(state) with
        | Some(error) -> failure(state)(error)
        | None ->
            state
            |> instantiateBuiltin(layout)
            |> finishBuiltinArguments(arguments)(lower)(runtimeStringRequested(state))
            |> releaseClosedBuiltinArgument(layout.kind)(arguments)

let recursive collectCallSpine expression =
    match expression with
        | ExprAt(_span, inner) -> collectCallSpine(inner)
        | ExprCall(function, argument, _whitespace, _layout) ->
            match collectCallSpine(function) with
                | CoreCallSpine { root = root, arguments = arguments } ->
                    CoreCallSpine(
                        root = root,
                        arguments = append(arguments)([argument])
                    )
        | root -> CoreCallSpine(root = root, arguments = [])

let recursive constructorParameterNames name count index =
    if index >= count
    then []
    else
        "__core_ctor_" + name + "_" + Ashes.Text.fromInt(index) :: constructorParameterNames(
            name,
            count,
            index + 1
        )

let recursive applyConstructorParameters expression parameters =
    match parameters with
        | [] -> expression
        | parameter :: rest ->
            applyConstructorParameters(
                ExprCall(
                    expression,
                    ExprVar(parameter),
                    false,
                    callArgumentsInline
                ),
                rest
            )

let recursive wrapConstructorParameters parameters body =
    match parameters with
        | [] -> body
        | parameter :: rest ->
            ExprLambda(
                parameter,
                wrapConstructorParameters(rest)(body),
                None
            )

let constructorLambda layout =
    match layout with
        | CoreConstructorLayout { name = name } ->
            let parameters =
                constructorParameterNames(name)(constructorArity(layout))(0)
            in
                parameters
                |> applyConstructorParameters(ExprVar(name))
                |> wrapConstructorParameters(parameters)

let builtinLambda layout =
    match layout with
        | CoreBuiltinLayout { moduleName = moduleName, memberName = memberName } ->
            0
            |> constructorParameterNames("builtin_" + memberName)(builtinArity(layout))
            |> (given (parameters) ->
                parameters
                |> applyConstructorParameters(ExprQualifiedVar(moduleName)(memberName))
                |> wrapConstructorParameters(parameters))

let tryLowerConstructorCall expression lower state =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprVar(name), arguments = arguments } ->
            match constructorLayout(name)(state) with
                | None -> None
                | Some(layout) ->
                    if coreListLength(arguments) == constructorArity(layout)
                    then
                        state
                        |> lowerConstructor(layout)(arguments)(lower)
                        |> Some
                    else None
        | _ -> None

let tryLowerBuiltinCall expression lower state =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprQualifiedVar(moduleName, memberName), arguments = arguments } ->
            match builtinLayout(moduleName)(memberName)(state) with
                | None -> None
                | Some(layout) ->
                    if coreListLength(arguments) == builtinArity(layout)
                    then
                        state
                        |> lowerBuiltin(layout)(arguments)(lower)
                        |> Some
                    else None
        | _ -> None

let externalLayout name state =
    match state with
        | CoreLoweringState { externalLayouts = layouts } -> tryFindExternalLayout(name)(layouts)

let recursive buildExternalParameterNameList count index =
    if index >= count
    then []
    else "__ext_param_" + Ashes.Text.fromInt(index) :: buildExternalParameterNameList(count)(index + 1)

let recursive applyExternalParameters expression parameters =
    match parameters with
        | [] -> expression
        | parameter :: rest ->
            applyExternalParameters(
                ExprCall(
                    expression,
                    ExprVar(parameter),
                    false,
                    callArgumentsInline
                )
            )(rest)

let recursive wrapExternalParameters parameters body =
    match parameters with
        | [] -> body
        | parameter :: rest ->
            ExprLambda(
                parameter,
                wrapExternalParameters(rest)(body),
                None
            )

let externalWrapperLambda layout =
    match layout with
        | CoreExternalFunctionLayout { name = name, abi = abi } ->
            match abi with
                | ExternalFunctionAbi { parameters = parameters } ->
                    let inputCount = externalInputParameterCount(parameters)
                    in
                        if inputCount == 0
                        then
                            ExprLambda(
                                "__ext_unit",
                                ExprCall(ExprVar(name))(ExprVar("__ext_unit"))(false)(callArgumentsInline),
                                None
                            )
                        else
                            let paramNames = buildExternalParameterNameList(inputCount)(0)
                            in
                                paramNames
                                |> applyExternalParameters(ExprVar(name))
                                |> wrapExternalParameters(paramNames)

let finishCoreExternalReference layout lower state =
    match layout with
        | CoreExternalFunctionLayout { name = name, abi = abi } ->
            match abi with
                | ExternalFunctionAbi { directOnly = directOnly } ->
                    if directOnly
                    then failure(state)(CoreExternalDirectOnlyViolation(name))
                    else
                        lower(externalWrapperLambda(layout))(state)

let unwrapNullaryExternalArgs expectedCount arguments =
    if expectedCount == 0
    then
        match arguments with
            | ExprVar("Unit") :: [] -> []
            | other -> other
    else arguments

let recursive emitInstructions (instructions: List(IrInstructionKind)) state =
    match instructions with
        | [] -> state
        | inst :: rest ->
            state
            |> emit(inst)
            |> emitInstructions(rest)

let lowerExternalCallArguments abi arguments lower state =
    match lowerCoreValues(arguments)(lower)(state) with
        | LoweredCoreValues { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValues { state = valuesState, temps = argTemps, semanticTypes = argTypes, error = None } ->
            match valuesState with
                | CoreLoweringState { nextTemp = startTemp, nextLocal = startLocal } ->
                    match emitDirectExternalCall(abi)(startTemp)(startLocal)(argTemps)(argTypes) with
                        | CoreExternalLoweringEmission { error = Some(err) } -> failure(valuesState)(UnsupportedCoreExternalLowering(err))
                        | CoreExternalLoweringEmission { instructions = callInstrs, nextTemp = endTemp, nextLocal = endLocal, resultTemp = resTemp, resultType = resType, error = None } ->
                            let emittedState =
                                valuesState
                                |> emitInstructions(callInstrs)
                                |> withNextTemp(endTemp)
                                |> withNextLocal(endLocal)
                            in success(resTemp)(resType)(emittedState)

// A direct external call under the resource contract of its parameters: the arguments are
// checked before lowering and their bindings released after it.
let lowerExternalCallWithResources (abi: ExternalFunctionAbi) arguments lower state =
    match checkExternalResourceArguments(abi)(externalInputParameters(abi.parameters))(arguments)(state) with
        | Some(error) -> failure(state)(error)
        | None ->
            state
            |> lowerExternalCallArguments(abi)(arguments)(lower)
            |> applyLoweredExternalResourceTransfers(abi)(arguments)

let tryLowerExternalCall expression lower state =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprVar(name), arguments = arguments } ->
            match externalLayout(name)(state) with
                | None -> None
                | Some(layout) ->
                    match layout with
                        | CoreExternalFunctionLayout { abi = abi } ->
                            match abi with
                                | ExternalFunctionAbi { parameters = parameters } ->
                                    let expectedCount = externalInputParameterCount(parameters)
                                    in
                                        let normalizedArgs = unwrapNullaryExternalArgs(expectedCount)(arguments)
                                        in
                                            if coreListLength(normalizedArgs) == expectedCount
                                            then
                                                state
                                                |> lowerExternalCallWithResources(abi)(normalizedArgs)(lower)
                                                |> Some
                                            else None
        | CoreCallSpine { root = ExprQualifiedVar(_moduleName, name), arguments = arguments } ->
            match externalLayout(name)(state) with
                | None -> None
                | Some(layout) ->
                    match layout with
                        | CoreExternalFunctionLayout { abi = abi } ->
                            match abi with
                                | ExternalFunctionAbi { parameters = parameters } ->
                                    let expectedCount = externalInputParameterCount(parameters)
                                    in
                                        let normalizedArgs = unwrapNullaryExternalArgs(expectedCount)(arguments)
                                        in
                                            if coreListLength(normalizedArgs) == expectedCount
                                            then
                                                state
                                                |> lowerExternalCallArguments(abi)(normalizedArgs)(lower)
                                                |> Some
                                            else None
        | _ -> None

let recursive findNamedField (name: Str) (fields: List((Str, Expr))) =
    match fields with
        | [] -> None
        | (candidate, expression) :: rest ->
            if name == candidate
            then Some(expression)
            else findNamedField(name)(rest)

let recursive orderRecordArguments (typeName: Str) (fieldNames: List(Str)) (fields: List((Str, Expr))) reversed =
    match fieldNames with
        | [] -> CoreRecordArguments(expressions = reverse(reversed), error = None)
        | fieldName :: rest ->
            match findNamedField(fieldName)(fields) with
                | None ->
                    CoreRecordArguments(expressions = [], error = fieldName
                    |> UnknownCoreRecordField(typeName)
                    |> Some)
                | Some(expression) -> orderRecordArguments(typeName)(rest)(fields)(expression :: reversed)

let lowerRecord name fields lower state =
    match constructorLayout(name)(state) with
        | None -> failure(state)(UnknownLoweringBinding(name))
        | Some(CoreConstructorLayout { fieldNames = [] }) ->
            ""
            |> UnknownCoreRecordField(name)
            |> failure(state)
        | Some(CoreConstructorLayout { fieldNames = fieldNames } as layout) ->
            match orderRecordArguments(name)(fieldNames)(fields)([]) with
                | CoreRecordArguments { error = Some(error) } -> failure(state)(error)
                | CoreRecordArguments { expressions = expressions, error = None } ->
                    lowerConstructor(
                        layout,
                        expressions,
                        lower,
                        state
                    )

let emitRecordFieldLoad receiverTemp fieldType index tagless fresh =
    match fresh with
        | FreshTemp { state = state, temp = fieldTemp } ->
            state
            |> emit(GetAdtField(fieldTemp)(receiverTemp)(index)(tagless))
            |> success(fieldTemp)(resolveType(state)(fieldType))

let loadResolvedRecordField typeName fieldName receiverTemp fieldNames fieldTypes tagless typed =
    match typed with
        | (failedState, Some(error)) -> failure(failedState)(error)
        | (typedState, None) ->
            match findPatternField(fieldName)(fieldNames)(fieldTypes)(0) with
                | None ->
                    fieldName
                    |> UnknownCoreRecordField(typeName)
                    |> failure(typedState)
                | Some(CorePatternField { index = index, semanticType = fieldType }) ->
                    typedState
                    |> freshTemp
                    |> emitRecordFieldLoad(receiverTemp)(fieldType)(index)(tagless)

let finishRecordFieldShape typeName fieldName receiverTemp receiverType fieldNames shape =
    match shape with
        | CoreConstructorShape { state = state, layout = CoreConstructorLayout { tagless = tagless }, parameterTypes = fieldTypes, resultType = resultType } ->
            state
            |> bindType(receiverType)(resultType)
            |> loadResolvedRecordField(typeName)(fieldName)(receiverTemp)(fieldNames)(fieldTypes)(tagless)

let finishRecordFieldLayout typeName fieldName receiverTemp receiverType state layout =
    match layout with
        | None -> failure(state)(CoreRecordUpdateRequiresRecord(receiverType))
        | Some(CoreConstructorLayout { fieldNames = fieldNames } as constructor) ->
            state
            |> instantiateConstructor(constructor)
            |> finishRecordFieldShape(typeName)(fieldName)(receiverTemp)(receiverType)(fieldNames)

let finishRecordFieldAccess _receiverName fieldName lowered =
    match lowered with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = state, temp = receiverTemp, semanticType = receiverType, error = None } ->
            match resolveType(state)(receiverType) with
                | SemNamed(_symbolId, typeName, _arguments) ->
                    state
                    |> recordLayout(typeName)
                    |> finishRecordFieldLayout(typeName)(fieldName)(receiverTemp)(receiverType)(state)
                | other -> failure(state)(CoreRecordUpdateRequiresRecord(other))

// The receiver of a field access is read without the owned-read borrow: stage 0's
// `TryLowerRecordFieldLoad` loads the binding's slot directly and takes the field from it.
let lowerRecordFieldAccess receiverName fieldName state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            match lookupBinding(receiverName)(bindings) with
                | None -> failure(state)(UnknownLoweringBinding(receiverName + "." + fieldName))
                | Some(binding) ->
                    state
                    |> lowerBoundVariable((binding with ownedRead = false))
                    |> finishRecordFieldAccess(receiverName)(fieldName)

let finishUpdatedRecordField expectedType reversedTemps reversedTypes lowered =
    match lowered with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failedCoreValues(failedState)(error)
        | LoweredCoreValue { state = valueState, temp = temp, semanticType = semanticType, error = None } ->
            match bindType(expectedType)(semanticType)(valueState) with
                | (failedState, Some(error)) -> failedCoreValues(failedState)(error)
                | (typedState, None) ->
                    LoweredCoreValues(
                        state = typedState,
                        temps = temp :: reversedTemps,
                        semanticTypes = expectedType :: reversedTypes,
                        error = None
                    )

let loadUnchangedRecordField targetTemp index tagless fieldType state reversedTemps reversedTypes =
    match freshTemp(state) with
        | FreshTemp { state = loadState, temp = temp } ->
            LoweredCoreValues(
                state = emit(GetAdtField(temp)(targetTemp)(index)(tagless))(loadState),
                temps = temp :: reversedTemps,
                semanticTypes = fieldType :: reversedTypes,
                error = None
            )

let recursive lowerRecordUpdateFields fieldNames fieldTypes updates targetTemp index tagless lower reversedTemps reversedTypes state =
    match (fieldNames, fieldTypes) with
        | ([], []) -> finishCoreValues(state)(reversedTemps)(reversedTypes)
        | (fieldName :: fieldRest, fieldType :: typeRest) ->
            let loweredField =
                match findNamedField(fieldName)(updates) with
                    | None ->
                        loadUnchangedRecordField(
                            targetTemp,
                            index,
                            tagless,
                            fieldType,
                            state,
                            reversedTemps,
                            reversedTypes
                        )
                    | Some(expression) ->
                        state
                        |> lower(expression)
                        |> finishUpdatedRecordField(fieldType)(reversedTemps)(reversedTypes)
            in
                match loweredField with
                    | LoweredCoreValues { state = failedState, error = Some(error) } -> failedCoreValues(failedState)(error)
                    | LoweredCoreValues { state = nextState, temps = nextTemps, semanticTypes = nextTypes, error = None } ->
                        lowerRecordUpdateFields(
                            fieldRest,
                            typeRest,
                            updates,
                            targetTemp,
                            index + 1,
                            tagless,
                            lower,
                            nextTemps,
                            nextTypes,
                            nextState
                        )
        | _ -> failedCoreValues(state)(UnsupportedCoreLoweringExpression("record layout arity"))

let lowerTypedRecordUpdate layout resultType runtimeManaged fieldNames fieldTypes fields targetTemp lower typed =
    match (layout, typed) with
        | (_layout, (failedState, Some(error))) -> failure(failedState)(error)
        | (CoreConstructorLayout { tagless = tagless }, (typedState, None)) ->
            typedState
            |> lowerRecordUpdateFields(fieldNames)(fieldTypes)(fields)(targetTemp)(0)(tagless)(lower)([])([])
            |> finishConstructorAllocation(layout)(resultType)(runtimeManaged)

let finishRecordUpdateShape layout fieldNames fields targetTemp targetType lower shape =
    match shape with
        | CoreConstructorShape { state = state, parameterTypes = fieldTypes, resultType = resultType, constructorRuntimeManaged = runtimeManaged } ->
            state
            |> bindType(targetType)(resultType)
            |> lowerTypedRecordUpdate(layout)(resultType)(runtimeManaged)(fieldNames)(fieldTypes)(fields)(targetTemp)(lower)

let finishRecordUpdateLayout fields targetTemp targetType lower state layout =
    match layout with
        | None -> failure(state)(CoreRecordUpdateRequiresRecord(targetType))
        | Some(CoreConstructorLayout { fieldNames = fieldNames } as constructor) ->
            state
            |> instantiateConstructor(constructor)
            |> finishRecordUpdateShape(constructor)(fieldNames)(fields)(targetTemp)(targetType)(lower)

let finishRecordUpdate fields lower loweredTarget =
    match loweredTarget with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = state, temp = targetTemp, semanticType = targetType, error = None } ->
            match resolveType(state)(targetType) with
                | SemNamed(_symbolId, typeName, _arguments) ->
                    state
                    |> recordLayout(typeName)
                    |> finishRecordUpdateLayout(fields)(targetTemp)(targetType)(lower)(state)
                | other -> failure(state)(CoreRecordUpdateRequiresRecord(other))

let lowerRecordUpdate target fields lower state =
    state
    |> lower(target)
    |> finishRecordUpdate(fields)(lower)

let failedCoreBinary state error =
    LoweredCoreBinary(
        state = state,
        leftTemp = -1,
        leftType = SemNever,
        rightTemp = -1,
        rightType = SemNever,
        error = Some(error)
    )

let finishCoreBinary leftTemp leftType loweredRight =
    match loweredRight with
        | LoweredCoreValue { state = state, error = Some(error) } -> failedCoreBinary(state)(error)
        | LoweredCoreValue { state = state, temp = rightTemp, semanticType = rightType, error = None } ->
            LoweredCoreBinary(
                state = state,
                leftTemp = leftTemp,
                leftType = leftType,
                rightTemp = rightTemp,
                rightType = rightType,
                error = None
            )

let lowerCoreBinaryRight right lower loweredLeft =
    match loweredLeft with
        | LoweredCoreValue { state = state, error = Some(error) } -> failedCoreBinary(state)(error)
        | LoweredCoreValue { state = state, temp = leftTemp, semanticType = leftType, error = None } ->
            state
            |> lower(right)
            |> finishCoreBinary(leftTemp)(leftType)

let lowerCoreBinaryOperands left right lower state =
    state
    |> lower(left)
    |> lowerCoreBinaryRight(right)(lower)

let numericDefault semanticType =
    match semanticType with
        | SemFloat -> SemFloat
        | SemRune -> SemRune
        | SemBigInt -> SemBigInt
        | SemUInt(bits) -> SemUInt(bits)
        | SemString -> SemString
        | _ -> SemInt

let finishCoreBinaryBinding (binary: LoweredCoreBinary) result =
    match result with
        | (failedState, Some(error)) -> failedCoreBinary(failedState)(error)
        | (typedState, None) -> binary with state = typedState

let bindCoreBinaryLeftType resolvedRight binary resolvedLeft =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType } ->
            match resolvedLeft with
                | SemVariable(_id) ->
                    state
                    |> bindType(leftType)(numericDefault(resolvedRight))
                    |> finishCoreBinaryBinding(binary)
                | _ -> binary

let bindCoreBinaryLeft resolvedRight binary =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType, error = None } ->
            leftType
            |> resolveType(state)
            |> bindCoreBinaryLeftType(resolvedRight)(binary)
        | failed -> failed

let bindCoreBinaryRightType binary resolvedRight =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType, rightType = rightType, error = None } ->
            match resolvedRight with
                | SemVariable(_id) ->
                    state
                    |> bindType(rightType)(leftType
                    |> resolveType(state)
                    |> numericDefault)
                    |> finishCoreBinaryBinding(binary)
                | _ -> binary
        | failed -> failed

let finishCoreBinaryLeftBinding leftBound =
    match leftBound with
        | LoweredCoreBinary { state = state, rightType = rightType, error = None } ->
            rightType
            |> resolveType(state)
            |> bindCoreBinaryRightType(leftBound)
        | failed -> failed

let bindCoreBinaryRight binary =
    match binary with
        | LoweredCoreBinary { state = state, rightType = rightType, error = None } ->
            binary
            |> bindCoreBinaryLeft(resolveType(state)(rightType))
            |> finishCoreBinaryLeftBinding
        | failed -> failed

let withCoreBinaryLeftType leftType (typed: LoweredCoreBinary) = typed with leftType = leftType

let withCoreBinaryRightType rightType (typed: LoweredCoreBinary) = typed with rightType = rightType

let resolveCoreBinaryTypes state (typed: LoweredCoreBinary) =
    match typed with
        | LoweredCoreBinary { leftType = leftType, rightType = rightType } ->
            typed
            |> withCoreBinaryLeftType(resolveType(state)(leftType))
            |> withCoreBinaryRightType(resolveType(state)(rightType))

let resolvedCoreBinary binary =
    match bindCoreBinaryRight(binary) with
        | LoweredCoreBinary { state = state, error = None } as typed -> resolveCoreBinaryTypes(state)(typed)
        | failed -> failed

let operatorMismatch name binary =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType, rightType = rightType } ->
            rightType
            |> CoreOperatorTypeMismatch(name)(leftType)
            |> failure(state)

let emitCoreBinaryTarget kind semanticType binary =
    match binary with
        | LoweredCoreBinary { state = state, leftTemp = left, rightTemp = right, error = None } ->
            match freshTemp(state) with
                | FreshTemp { state = targetState, temp = target } ->
                    targetState
                    |> emit(kind(target)(left)(right))
                    |> success(target)(semanticType)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

let emitCoreFloatBinary kind binary = emitCoreBinaryTarget(kind)(SemFloat)(binary)

let emitCoreBoolBinary kind binary = emitCoreBinaryTarget(kind)(SemBool)(binary)

let reserveCoreTemps count state =
    match state with
        | CoreLoweringState { nextTemp = first } ->
            ReservedCoreTemps(
                state = withNextTemp(first + count)(state),
                first = first
            )

let maskCoreUInt bits lowered =
    match lowered with
        | LoweredCoreValue { state = state, error = Some(error) } -> failure(state)(error)
        | LoweredCoreValue { state = state, temp = raw, error = None } ->
            if bits == 64
            then success(raw)(SemUInt(bits))(state)
            else
                match reserveCoreTemps(2)(state) with
                    | ReservedCoreTemps { state = resultState, first = maskTemp } ->
                        resultState
                        |> emit(LoadConstInt(maskTemp)((1 << bits) - 1))
                        |> emit(AndInt(maskTemp + 1)(raw)(maskTemp))
                        |> success(maskTemp + 1)(SemUInt(bits))

let emitCoreUIntTarget kind bits binary =
    binary
    |> emitCoreBinaryTarget(kind)(SemUInt(bits))
    |> maskCoreUInt(bits)

let emitCoreUInt kind bits binary = emitCoreUIntTarget(kind)(bits)(binary)

let emitCoreDivision isUnsigned quotient left right state =
    if isUnsigned
    then
        emit(DivUInt(quotient)(left)(right))(state)
    else
        emit(DivInt(quotient)(left)(right))(state)

let emitCoreRemainder isUnsigned resultType binary =
    match binary with
        | LoweredCoreBinary { state = state, leftTemp = left, rightTemp = right, error = None } ->
            match reserveCoreTemps(3)(state) with
                | ReservedCoreTemps { state = remainderState, first = quotient } ->
                    remainderState
                    |> emitCoreDivision(isUnsigned)(quotient)(left)(right)
                    |> emit(MulInt(quotient + 1)(quotient)(right))
                    |> emit(SubInt(quotient + 2)(left)(quotient + 1))
                    |> success(quotient + 2)(resultType)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

let emitCoreBigIntComparison kind binary =
    match binary with
        | LoweredCoreBinary { state = state, leftTemp = left, rightTemp = right, error = None } ->
            match reserveCoreTemps(3)(state) with
                | ReservedCoreTemps { state = targetState, first = comparison } ->
                    targetState
                    |> emit(BigIntCompare(comparison)(left)(right))
                    |> emit(LoadConstInt(comparison + 1)(0))
                    |> emit(kind(comparison + 2)(comparison)(comparison + 1))
                    |> success(comparison + 2)(SemBool)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

let emitCoreBigIntBinary operation binary =
    emitCoreBinaryTarget(
        given (target) ->
            given (left) ->
                given (right) -> BigIntBinary(target)(left)(right)(operation)(false)
    )(SemBigInt)(binary)

let emitCoreConcat binary =
    emitCoreBinaryTarget(
        given (target) ->
            given (left) ->
                given (right) -> ConcatStr(target)(left)(right)(false)
    )(SemString)(binary)

let emitResolvedCoreAdd binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(AddInt)(SemInt)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreUInt(AddInt)(leftBits)(binary)
            else operatorMismatch("+")(binary)
        | LoweredCoreBinary { leftType = SemFloat, rightType = SemFloat } -> emitCoreFloatBinary(AddFloat)(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntBinary("add")(binary)
        | LoweredCoreBinary { leftType = SemString, rightType = SemString } -> emitCoreConcat(binary)
        | _ -> operatorMismatch("+")(binary)

let emitResolvedCoreSubtract binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(SubInt)(SemInt)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreUInt(SubInt)(leftBits)(binary)
            else operatorMismatch("-")(binary)
        | LoweredCoreBinary { leftType = SemFloat, rightType = SemFloat } -> emitCoreFloatBinary(SubFloat)(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntBinary("sub")(binary)
        | _ -> operatorMismatch("-")(binary)

let emitResolvedCoreMultiply binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(MulInt)(SemInt)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreUInt(MulInt)(leftBits)(binary)
            else operatorMismatch("*")(binary)
        | LoweredCoreBinary { leftType = SemFloat, rightType = SemFloat } -> emitCoreFloatBinary(MulFloat)(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntBinary("mul")(binary)
        | _ -> operatorMismatch("*")(binary)

let emitResolvedCoreDivide binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(DivInt)(SemInt)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreUInt(DivUInt)(leftBits)(binary)
            else operatorMismatch("/")(binary)
        | LoweredCoreBinary { leftType = SemFloat, rightType = SemFloat } -> emitCoreFloatBinary(DivFloat)(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntBinary("div")(binary)
        | _ -> operatorMismatch("/")(binary)

let emitResolvedCoreModulo binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreRemainder(false)(SemInt)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then
                binary
                |> emitCoreRemainder(true)(SemUInt(leftBits))
                |> maskCoreUInt(leftBits)
            else operatorMismatch("%")(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntBinary("mod")(binary)
        | _ -> operatorMismatch("%")(binary)

let emitResolvedCoreBitwise name intKind binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(intKind)(SemInt)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreUInt(intKind)(leftBits)(binary)
            else operatorMismatch(name)(binary)
        | _ -> operatorMismatch(name)(binary)

let emitResolvedCoreShift name intKind binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(intKind)(SemInt)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreUInt(intKind)(leftBits)(binary)
            else operatorMismatch(name)(binary)
        | _ -> operatorMismatch(name)(binary)

let emitResolvedCoreOrdered name intKind uintKind floatKind binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(intKind)(SemBool)(binary)
        | LoweredCoreBinary { leftType = SemRune, rightType = SemRune } -> emitCoreBoolBinary(intKind)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreBoolBinary(uintKind)(binary)
            else operatorMismatch(name)(binary)
        | LoweredCoreBinary { leftType = SemFloat, rightType = SemFloat } -> emitCoreBoolBinary(floatKind)(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntComparison(intKind)(binary)
        | _ -> operatorMismatch(name)(binary)

let emitResolvedCoreEquality name intKind floatKind stringKind binary =
    match binary with
        | LoweredCoreBinary { leftType = SemInt, rightType = SemInt } -> emitCoreBinaryTarget(intKind)(SemBool)(binary)
        | LoweredCoreBinary { leftType = SemRune, rightType = SemRune } -> emitCoreBoolBinary(intKind)(binary)
        | LoweredCoreBinary { leftType = SemBool, rightType = SemBool } -> emitCoreBoolBinary(intKind)(binary)
        | LoweredCoreBinary { leftType = SemUInt(leftBits), rightType = SemUInt(rightBits) } ->
            if leftBits == rightBits
            then emitCoreBoolBinary(intKind)(binary)
            else operatorMismatch(name)(binary)
        | LoweredCoreBinary { leftType = SemFloat, rightType = SemFloat } -> emitCoreBoolBinary(floatKind)(binary)
        | LoweredCoreBinary { leftType = SemBigInt, rightType = SemBigInt } -> emitCoreBigIntComparison(intKind)(binary)
        | LoweredCoreBinary { leftType = SemString, rightType = SemString } -> emitCoreBoolBinary(stringKind)(binary)
        | _ -> operatorMismatch(name)(binary)

let emitResolvedCoreBinary operator binary =
    match operator with
        | CoreAddOperator -> emitResolvedCoreAdd(binary)
        | CoreSubtractOperator -> emitResolvedCoreSubtract(binary)
        | CoreMultiplyOperator -> emitResolvedCoreMultiply(binary)
        | CoreDivideOperator -> emitResolvedCoreDivide(binary)
        | CoreModuloOperator -> emitResolvedCoreModulo(binary)
        | CoreBitwiseAndOperator -> emitResolvedCoreBitwise("&")(AndInt)(binary)
        | CoreBitwiseOrOperator -> emitResolvedCoreBitwise("|")(OrInt)(binary)
        | CoreBitwiseXorOperator -> emitResolvedCoreBitwise("^")(XorInt)(binary)
        | CoreShiftLeftOperator -> emitResolvedCoreShift("<<")(ShlInt)(binary)
        | CoreShiftRightOperator -> emitResolvedCoreShift(">>")(ShrInt)(binary)
        | CoreGreaterOperator -> emitResolvedCoreOrdered(">")(CmpIntGt)(CmpUIntGt)(CmpFloatGt)(binary)
        | CoreGreaterOrEqualOperator -> emitResolvedCoreOrdered(">=")(CmpIntGe)(CmpUIntGe)(CmpFloatGe)(binary)
        | CoreLessOperator -> emitResolvedCoreOrdered("<")(CmpIntLt)(CmpUIntLt)(CmpFloatLt)(binary)
        | CoreLessOrEqualOperator -> emitResolvedCoreOrdered("<=")(CmpIntLe)(CmpUIntLe)(CmpFloatLe)(binary)
        | CoreEqualOperator -> emitResolvedCoreEquality("==")(CmpIntEq)(CmpFloatEq)(CmpStrEq)(binary)
        | CoreNotEqualOperator -> emitResolvedCoreEquality("!=")(CmpIntNe)(CmpFloatNe)(CmpStrNe)(binary)

let setBinaryLeft temp semanticType (binary: LoweredCoreBinary) state = binary with state = state, leftTemp = temp, leftType = semanticType

let coerceCoreFloatZero binary state =
    match freshTemp(state) with
        | FreshTemp { state = zeroState, temp = zero } ->
            zeroState
            |> emit(LoadConstFloat(zero)(0.0))
            |> setBinaryLeft(zero)(SemFloat)(binary)

let coerceCoreUIntZero bits binary state =
    match freshTemp(state) with
        | FreshTemp { state = zeroState, temp = zero } ->
            zeroState
            |> emit(LoadConstInt(zero)(0))
            |> setBinaryLeft(zero)(SemUInt(bits))(binary)

let coerceCoreBigIntZero binary state =
    match reserveCoreTemps(2)(state) with
        | ReservedCoreTemps { state = zeroState, first = zero } ->
            zeroState
            |> emit(LoadConstInt(zero)(0))
            |> emit(BigIntFromInt(zero + 1)(zero)(false))
            |> setBinaryLeft(zero + 1)(SemBigInt)(binary)

let coerceCoreNegationZero binary =
    match binary with
        | LoweredCoreBinary { state = state, leftType = SemInt, rightType = rightType, error = None } ->
            match resolveType(state)(rightType) with
                | SemFloat -> coerceCoreFloatZero(binary)(state)
                | SemBigInt -> coerceCoreBigIntZero(binary)(state)
                | SemUInt(bits) -> coerceCoreUIntZero(bits)(binary)(state)
                | _ -> binary
        | _ -> binary

let recursive isCoreZeroExpression expression =
    match expression with
        | ExprAt(_span, inner) -> isCoreZeroExpression(inner)
        | ExprInt(0) -> true
        | _ -> false

// `+` over two still-unresolved type variables (`first + second` inside a nested recursive group
// whose element type only settles later, like `Text.join`'s reduce learning `Str` from `""`) must
// not be defaulted eagerly: the operands are unified with each other, an `AddInt` is emitted
// speculatively and recorded under its target temp (temps survive `pruneDeadCaptures`, positions
// do not), the shared variable flows on as the result type, and `buildProgram` swaps the
// instruction for the resolved type's real form once the substitution is final. Every other
// operand shape still resolves immediately through `resolvedCoreBinary`.
let emitDeferredCoreAdd binary =
    match binary with
        | LoweredCoreBinary { state = state, leftTemp = left, leftType = leftType, rightTemp = right, rightType = rightType, error = None } ->
            match bindType(leftType)(rightType)(state) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (typedState, None) ->
                    match freshTemp(typedState) with
                        | FreshTemp { state = targetState, temp = target } ->
                            match targetState with
                                | CoreLoweringState { pendingOperatorDefaults = pending } ->
                                    let recorded = targetState with pendingOperatorDefaults = (target, Ashes.Internal.deepCopy(leftType)) :: pending
                                    in
                                        recorded
                                        |> emit(AddInt(target)(left)(right))
                                        |> success(target)(leftType)
        | LoweredCoreBinary { state = state, error = Some(error) } -> failure(state)(error)

let emitPreparedCoreBinary operator binary =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType, rightType = rightType, error = None } ->
            match operator with
                | CoreAddOperator ->
                    match (resolveType(state)(leftType), resolveType(state)(rightType)) with
                        | (SemVariable(_leftId), SemVariable(_rightId)) -> emitDeferredCoreAdd(binary)
                        | _ ->
                            binary
                            |> resolvedCoreBinary
                            |> emitResolvedCoreBinary(operator)
                | _ ->
                    binary
                    |> resolvedCoreBinary
                    |> emitResolvedCoreBinary(operator)
        | _ ->
            binary
            |> resolvedCoreBinary
            |> emitResolvedCoreBinary(operator)

let resolveDeferredAddKind state semanticType target left right =
    match resolveType(state)(semanticType) with
        | SemString -> ConcatStr(target)(left)(right)(false)
        | SemFloat -> AddFloat(target)(left)(right)
        | SemBigInt -> BigIntBinary(target)(left)(right)("add")(false)
        | _ -> AddInt(target)(left)(right)

let recursive rewriteDeferredAdd state pendingTarget semanticType instructions =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = AddInt(target, left, right), location = location } as instruction) :: rest ->
            if target == pendingTarget
            then IrInstruction(instruction = resolveDeferredAddKind(state)(semanticType)(target)(left)(right), location = location) :: rest
            else instruction :: rewriteDeferredAdd(state)(pendingTarget)(semanticType)(rest)
        | instruction :: rest -> instruction :: rewriteDeferredAdd(state)(pendingTarget)(semanticType)(rest)

let recursive applyDeferredAdds state pending instructions =
    match pending with
        | [] -> instructions
        | (pendingTarget, semanticType) :: rest ->
            instructions
            |> rewriteDeferredAdd(state)(pendingTarget)(semanticType)
            |> applyDeferredAdds(state)(rest)

let recursive sealedDeferredFor (label: Str) (sealed: List((Str, Int, SemanticType))) =
    match sealed with
        | [] -> []
        | (candidate, position, semanticType) :: rest ->
            if candidate == label
            then (position, semanticType) :: sealedDeferredFor(label)(rest)
            else sealedDeferredFor(label)(rest)

let recursive applySealedDeferredAdds state sealed functions =
    match functions with
        | [] -> []
        | (IrFunction { label = label, instructions = instructions } as function) :: rest ->
            (function with instructions = applyDeferredAdds(state)(sealedDeferredFor(label)(sealed))(instructions)) :: applySealedDeferredAdds(state)(sealed)(rest)

let prepareCoreBinary operator left binary =
    match operator with
        | CoreSubtractOperator ->
            if isCoreZeroExpression(left)
            then coerceCoreNegationZero(binary)
            else binary
        | _ -> binary

let lowerCoreBinary operator left right lower state =
    state
    |> lowerCoreBinaryOperands(left)(right)(lower)
    |> prepareCoreBinary(operator)(left)
    |> emitPreparedCoreBinary(operator)

let finishCoreLogicalNot lowered =
    match lowered with
        | LoweredCoreValue { state = state, temp = operand, semanticType = semanticType, error = None } ->
            match resolveType(state)(semanticType) with
                | SemBool ->
                    match reserveCoreTemps(2)(state) with
                        | ReservedCoreTemps { state = targetState, first = falseTemp } ->
                            targetState
                            |> emit(LoadConstBool(falseTemp)(false))
                            |> emit(CmpIntEq(falseTemp + 1)(operand)(falseTemp))
                            |> success(falseTemp + 1)(SemBool)
                | other ->
                    other
                    |> CoreOperatorTypeMismatch("not")(other)
                    |> failure(state)
        | LoweredCoreValue { state = state, error = Some(error) } -> failure(state)(error)

let finishCoreBitwiseNot lowered =
    match lowered with
        | LoweredCoreValue { state = state, temp = operand, semanticType = semanticType, error = None } ->
            match resolveType(state)(semanticType) with
                | SemInt ->
                    match reserveCoreTemps(2)(state) with
                        | ReservedCoreTemps { state = targetState, first = maskTemp } ->
                            targetState
                            |> emit(LoadConstInt(maskTemp)(-1))
                            |> emit(XorInt(maskTemp + 1)(operand)(maskTemp))
                            |> success(maskTemp + 1)(SemInt)
                | SemUInt(bits) ->
                    match reserveCoreTemps(2)(state) with
                        | ReservedCoreTemps { state = targetState, first = maskTemp } ->
                            targetState
                            |> emit(LoadConstInt(maskTemp)(if bits == 64
                            then -1
                            else (1 << bits) - 1))
                            |> emit(XorInt(maskTemp + 1)(operand)(maskTemp))
                            |> success(maskTemp + 1)(SemUInt(bits))
                | other ->
                    other
                    |> CoreOperatorTypeMismatch("~")(other)
                    |> failure(state)
        | LoweredCoreValue { state = state, error = Some(error) } -> failure(state)(error)

let lowerCoreLogicalNot operand lower state =
    state
    |> lower(operand)
    |> finishCoreLogicalNot

let lowerCoreBitwiseNot operand lower state =
    state
    |> lower(operand)
    |> finishCoreBitwiseNot

let recursive parseDecimalDigits remaining value =
    match Ashes.Text.unconsText(remaining) with
        | None -> value
        | Some((digit, rest)) -> parseDecimalDigits(rest)(value * 10 + Ashes.Text.firstByteOf(digit) - 48)

let bigIntFitsLength digits digitCount =
    if digitCount < 19
    then true
    else
        if digitCount == 19
        then Ashes.Text.compare(digits)("9223372036854775807") <= 0
        else false

let bigIntFitsInt digits =
    digits
    |> Ashes.Text.length
    |> bigIntFitsLength(digits)

let emitCoreBigIntConstant value state =
    match reserveCoreTemps(2)(state) with
        | ReservedCoreTemps { state = targetState, first = constantTemp } ->
            targetState
            |> emit(LoadConstInt(constantTemp)(value))
            |> emit(BigIntFromInt(constantTemp + 1)(constantTemp)(false))
            |> success(constantTemp + 1)(SemBigInt)

let decimalChunk digits =
    (parseDecimalDigits(Ashes.Text.take(digits)(18))(0), Ashes.Text.drop(digits)(18))

let recursive emitCoreBigIntChunks remaining accumulator state =
    match remaining with
        | "" -> success(accumulator)(SemBigInt)(state)
        | _ ->
            match (decimalChunk(remaining), reserveCoreTemps(6)(state)) with
                | ((chunk, rest), ReservedCoreTemps { state = targetState, first = baseInt }) ->
                    targetState
                    |> emit(LoadConstInt(baseInt)(1000000000000000000))
                    |> emit(BigIntFromInt(baseInt + 1)(baseInt)(false))
                    |> emit(BigIntBinary(baseInt + 2)(accumulator)(baseInt + 1)("mul")(false))
                    |> emit(LoadConstInt(baseInt + 3)(chunk))
                    |> emit(BigIntFromInt(baseInt + 4)(baseInt + 3)(false))
                    |> emit(BigIntBinary(baseInt + 5)(baseInt + 2)(baseInt + 4)("add")(false))
                    |> emitCoreBigIntChunks(rest)(baseInt + 5)

let finishCoreBigIntHead rest lowered =
    match lowered with
        | LoweredCoreValue { error = None } as value ->
            match value with
                | LoweredCoreValue { state = state, temp = accumulator } ->
                    emitCoreBigIntChunks(
                        rest,
                        accumulator,
                        state
                    )
        | LoweredCoreValue { state = state, error = Some(error) } -> failure(state)(error)

let bigIntFirstLength digitCount =
    match digitCount % 18 with
        | 0 -> 18
        | remainder -> remainder

let lowerCoreLargeBigIntFromFirst digits state firstLength =
    state
    |> emitCoreBigIntConstant(parseDecimalDigits(Ashes.Text.take(digits)(firstLength))(0))
    |> finishCoreBigIntHead(Ashes.Text.drop(digits)(firstLength))

let lowerCoreLargeBigInt digits state =
    digits
    |> Ashes.Text.length
    |> bigIntFirstLength
    |> lowerCoreLargeBigIntFromFirst(digits)(state)

let lowerCoreBigInt digits state =
    if bigIntFitsInt(digits)
    then
        emitCoreBigIntConstant(parseDecimalDigits(digits)(0))(state)
    else lowerCoreLargeBigInt(digits)(state)

let finishCoreConstructorReference layout lower state =
    if constructorArity(layout) == 0
    then lowerConstructor(layout)([])(lower)(state)
    else
        lower(constructorLambda(layout))(state)

let finishCoreBuiltinReference layout lower state =
    if builtinArity(layout) == 0
    then lowerBuiltin(layout)([])(lower)(state)
    else
        lower(builtinLambda(layout))(state)

// A name not found anywhere ordinary (no local/top-level binding, no constructor, no external
// declaration) is either a genuine undefined identifier, or — under Model A's sequential top-level
// scoping — a binding that DOES exist, just later in the file, not yet visible from here.
// state.topLevelNames (every top-level value-binding name in the whole program, collected once up
// front by each whole-program entry point) is exactly what distinguishes the two: mirrors stage-0's
// LowerVarUnbound/_topLevelBindingNames specialization (Lowering.cs:2844). Expression-only entry
// points (lowerCoreExpression*) never populate topLevelNames, so this never fires for them — there
// is no "later in the file" to be forward-referencing without a whole program.
let lowerCoreVariable name lower state =
    match state with
        | CoreLoweringState { bindings = bindings, externalLayouts = externalLayouts, topLevelNames = topLevelNames } ->
            match lookupBinding(name)(bindings) with
                | Some(binding) -> lowerBoundVariable(binding)(state)
                | None ->
                    match constructorLayout(name)(state) with
                        | Some(layout) -> finishCoreConstructorReference(layout)(lower)(state)
                        | None ->
                            match tryFindExternalLayout(name)(externalLayouts) with
                                | Some(extLayout) -> finishCoreExternalReference(extLayout)(lower)(state)
                                | None ->
                                    if containsName(name)(topLevelNames)
                                    then failure(state)(ForwardTopLevelReference(name))
                                    else failure(state)(UnknownLoweringBinding(name))

let lowerCoreQualifiedVariable moduleName memberName lower state =
    match builtinLayout(moduleName)(memberName)(state) with
        | Some(layout) -> finishCoreBuiltinReference(layout)(lower)(state)
        | None ->
            match state with
                | CoreLoweringState { externalLayouts = externalLayouts } ->
                    match tryFindExternalLayout(memberName)(externalLayouts) with
                        | Some(extLayout) -> finishCoreExternalReference(extLayout)(lower)(state)
                        | None -> lowerRecordFieldAccess(moduleName)(memberName)(state)

let lowerQualified moduleName memberName lower state = lowerCoreQualifiedVariable(moduleName)(memberName)(lower)(state)

let recursive emitSnapshotGlobals currentK globalCount frameTemp state =
    if currentK >= globalCount
    then state
    else
        match freshTemp(state) with
            | FreshTemp { state = nextState, temp = snapTemp } ->
                nextState
                |> emit(LoadCapabilityHandler(snapTemp)(currentK))
                |> emit(StoreMemOffset(frameTemp)(currentK * 8)(snapTemp))
                |> emitSnapshotGlobals(currentK + 1)(globalCount)(frameTemp)

// After a dynamic perform's arm closure returns, checks the well-known "pending post" capability
// global (index globalCount, the same slot array StoreCapabilityHandler/LoadCapabilityHandler
// already manage) a one-shot-resume arm stores its post continuation into
// (buildOperationArmClosure's one-shot-let path). When non-zero, conses it onto the handler
// frame's posts list (the same 16-byte head/tail cell shape allocateListCell already builds for
// ordinary list cons) and resets the register to zero so a later, unrelated perform site never
// mistakes a stale value for its own post.
let collectCapabilityPost globalCount capabilityIndex state =
    match freshTemp(state) with
        | FreshTemp { state = regState, temp = postRegTemp } ->
            match regState
            |> emit(LoadCapabilityHandler(postRegTemp)(globalCount))
            |> freshTemp with
                | FreshTemp { state = zeroState, temp = zeroTemp } ->
                    match zeroState
                    |> emit(LoadConstInt(zeroTemp)(0))
                    |> freshTemp with
                        | FreshTemp { state = cmpState, temp = hasPostTemp } ->
                            match freshLabel("capability_post_skip")(cmpState) with
                                | FreshLabel { state = labeledState, label = skipLabel } ->
                                    let checkedState =
                                        labeledState
                                        |> emit(CmpIntNe(hasPostTemp)(postRegTemp)(zeroTemp))
                                        |> emit(JumpIfFalse(hasPostTemp)(skipLabel))
                                    in
                                        match freshTemp(checkedState) with
                                            | FreshTemp { state = frameState, temp = frameTemp } ->
                                                let withFrame =
                                                    emit(LoadCapabilityHandler(frameTemp)(capabilityIndex))(frameState)
                                                in
                                                    match freshTemp(withFrame) with
                                                        | FreshTemp { state = headAddrState, temp = postsHeadAddrTemp } ->
                                                            let withHeadAddr =
                                                                emit(LoadMemOffset(postsHeadAddrTemp)(frameTemp)(globalCount * 8))(headAddrState)
                                                            in
                                                                match freshTemp(withHeadAddr) with
                                                                    | FreshTemp { state = prevHeadState, temp = previousHeadTemp } ->
                                                                        let withPrevHead =
                                                                            emit(LoadMemOffset(previousHeadTemp)(postsHeadAddrTemp)(0))(prevHeadState)
                                                                        in
                                                                            match allocateListCell(postRegTemp)(previousHeadTemp)(SemNever)(withPrevHead) with
                                                                                | LoweredCoreValue { state = failedCellState, error = Some(error) } -> failure(failedCellState)(error)
                                                                                | LoweredCoreValue { state = cellState, temp = cellTemp, error = None } ->
                                                                                    cellState
                                                                                    |> emit(StoreMemOffset(postsHeadAddrTemp)(0)(cellTemp))
                                                                                    |> emit(StoreCapabilityHandler(globalCount)(zeroTemp))
                                                                                    |> emit(Label(skipLabel))
                                                                                    |> success(-1)(SemNever)

let lowerPerform operation lower state =
    match collectCallSpine(operation) with
        | CoreCallSpine { root = ExprQualifiedVar(capName, opName), arguments = arguments } ->
            if capName == "Stop"
            then
                if opName == "stop"
                then
                    match arguments with
                        | arg :: [] ->
                            match lower(arg)(state) with
                                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                                | LoweredCoreValue { state = argState, temp = argTemp, error = None } ->
                                    argState
                                    |> emit(RequestServerStop(argTemp))
                                    |> success(argTemp)(SemNamed(0)("Unit")([]))
                        | _ ->
                            arguments
                            |> coreListLength
                            |> CoreBuiltinArityMismatch("Stop")("stop")(1)
                            |> failure(state)
                else failure(state)(UnknownLoweringBinding("Stop." + opName))
            else
                match state with
                    | CoreLoweringState { capabilityLayouts = capLayouts, staticProviders = providers, capabilityGlobalCount = globalCount } ->
                        // No whole-program entry point wires real ProviderInfo into `providers` yet
                        // (see docs/md/future/SELF_HOSTING.md's "generic capability evidence" item),
                        // so this call site has no per-call-site type argument to disambiguate with
                        // — `[]` correctly matches by capability name alone, ambiguous only when two
                        // registered providers for the same name genuinely differ.
                        match findStaticProvider(capName)([])(providers) with
                            | Some(provider) ->
                                match findProviderOperation(opName)(provider.operations) with
                                    | Some(implExpr) ->
                                        match lower(implExpr)(state) with
                                            | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                                            | LoweredCoreValue { state = implState, temp = implTemp, error = None } ->
                                                match lowerCoreValues(arguments)(lower)(implState) with
                                                    | LoweredCoreValues { state = argFailedState, error = Some(error) } -> failure(argFailedState)(error)
                                                    | LoweredCoreValues { state = valuesState, temps = argTemps, error = None } ->
                                                        match valuesState with
                                                            | CoreLoweringState { nextTemp = startTemp, nextLocal = startLocal } ->
                                                                match []
                                                                |> SemNamed(0)("Unit")
                                                                |> emitStaticProviderCall(provider)(opName)(implTemp)(startTemp)(startLocal)(argTemps) with
                                                                    | CoreCapabilityPerformEmission { instructions = callInstrs, nextTemp = endTemp, nextLocal = endLocal, resultTemp = resTemp, semanticType = resType, error = None } ->
                                                                        let emittedState =
                                                                            valuesState
                                                                            |> emitInstructions(callInstrs)
                                                                            |> withNextTemp(endTemp)
                                                                            |> withNextLocal(endLocal)
                                                                        in success(resTemp)(resType)(emittedState)
                                                                    | _ ->
                                                                        opName
                                                                        |> CoreUnhandledCapabilityOperation(capName)
                                                                        |> failure(valuesState)
                                    | None ->
                                        opName
                                        |> CoreUnhandledCapabilityOperation(capName)
                                        |> failure(state)
                            | None ->
                                match findCapabilityLayout(capName)(capLayouts) with
                                    | Some(layout) ->
                                        match findCapabilityOperationIndex(opName)(layout.operations) with
                                            | Some(opIndex) ->
                                                match lowerCoreValues(arguments)(lower)(state) with
                                                    | LoweredCoreValues { state = argFailedState, error = Some(error) } -> failure(argFailedState)(error)
                                                    | LoweredCoreValues { state = valuesState, temps = argTemps, error = None } ->
                                                        match valuesState with
                                                            | CoreLoweringState { nextTemp = startTemp, nextLocal = startLocal } ->
                                                                let resType = SemNamed(0)("Unit")([])
                                                                in
                                                                    match emitDynamicPerform(capName)(opName)(layout.index)(opIndex)(globalCount)(startTemp)(startLocal)(argTemps)(resType) with
                                                                        | CoreCapabilityPerformEmission { instructions = performInstrs, nextTemp = endTemp, nextLocal = endLocal, resultTemp = resTemp, semanticType = resultSemType, error = None } ->
                                                                            let emittedState =
                                                                                valuesState
                                                                                |> emitInstructions(performInstrs)
                                                                                |> withNextTemp(endTemp)
                                                                                |> withNextLocal(endLocal)
                                                                            in
                                                                                match collectCapabilityPost(globalCount)(layout.index)(emittedState) with
                                                                                    | LoweredCoreValue { state = failedPostState, error = Some(error) } -> failure(failedPostState)(error)
                                                                                    | LoweredCoreValue { state = postCollectedState, error = None } -> success(resTemp)(resultSemType)(postCollectedState)
                                                                        | _ ->
                                                                            opName
                                                                            |> CoreUnhandledCapabilityOperation(capName)
                                                                            |> failure(valuesState)
                                            | None ->
                                                opName
                                                |> CoreUnhandledCapabilityOperation(capName)
                                                |> failure(state)
                                    | None ->
                                        opName
                                        |> CoreUnhandledCapabilityOperation(capName)
                                        |> failure(state)
        | _ -> failure(state)(PerformTargetNotCapabilityOperation("'perform' must be applied to a capability operation call."))

// An operation arm's body must call `resume` exactly once; only the tail-position form
// `resume(e)` (the arm body itself, unwrapped of span nodes) is supported so far — it rewrites to
// plain `e`, no continuation-splitting needed. `let x = resume(v) in body`, a match/if scrutinee
// resume, and every other position stage 0 also accepts are not yet ported (they need the
// one-shot post-resume continuation machinery `LowerHandleFoldPosts` builds, which selfhost
// doesn't have yet); an arm using one of those is rejected with UnsupportedOperationArmResume
// rather than silently producing wrong IR.
let recursive unspanForResumeCheck expression =
    match expression with
        | ExprAt(_span, inner) -> unspanForResumeCheck(inner)
        | other -> other

let tailResumeArgument body =
    match unspanForResumeCheck(body) with
        | ExprCall(function, argument, _whitespace, _layout) ->
            match unspanForResumeCheck(function) with
                | ExprVar("resume") -> Some(argument)
                | _ -> None
        | _ -> None

// Handed to finishLetValue/lowerPreparedRecursiveGroupWith wherever the real "body" is supplied by
// a continuation lower instead of a literal expression (the rest of a top-level program's items in
// lowerCoreProgramItems, or the rest of an operation arm's body in resolveOperationArmBody below)
// — the continuation lower ignores it. A bare int literal needs no environment/binding resolution,
// so it stays inert even if a future change accidentally lowers it before the continuation
// intercepts it.
let topLevelContinuationBody = ExprInt(0)

// Pure, no lowering: does `expr` reference the special `resume` marker anywhere, unshadowed.
// Reuses collectFree since it already tracks shadowing via `bound` — `resume` is never a real
// outer binding, so its presence in the free set means the expression calls or mentions it.
let exprReferencesResume expr =
    []
    |> collectFree(expr)([])
    |> containsName("resume")

// Pure, no lowering: does any case body or guard in a one-shot-scrutinee match reference `resume`
// again — a one-shot `resume` may run at most once per path (matching stage-0's
// TryRewriteResumeOneShotMatch check), and once the scrutinee itself is the resume call, every
// case becomes part of the SINGLE post continuation (there is no further per-case independent
// resolution the way ordinary case-body recursion allows), so any case resuming a second time is
// rejected rather than silently producing wrong IR.
let recursive matchCasesReferenceResume cases =
    match cases with
        | [] -> false
        | (_pattern, caseBody, guard) :: rest ->
            let guardReferencesResume =
                match guard with
                    | Some(guardExpr) -> exprReferencesResume(guardExpr)
                    | None -> false
            in
                if exprReferencesResume(caseBody)
                then true
                else
                    if guardReferencesResume
                    then true
                    else matchCasesReferenceResume(rest)

// Pure, no lowering: a resume-free stand-in for `body`, used only so lowerLambda's own capture
// analysis (`collectFree`) sees an operation-arm closure's true free variables — the placeholder
// itself is never lowered. Mirrors resolveOperationArmBody's shape recognition one layer at a
// time: a non-resuming let/let-recursive prefix survives with its own body replaced by the
// placeholder for what follows; a one-shot `let x = resume(v) in body` becomes the same
// resume-free tuple-of-value-and-post-lambda shape the leaf case actually builds; anything else
// falls back to its own (already resume-free, or rejected downstream) tail shape.
let recursive armBodyCapturePlaceholder body =
    match unspanForResumeCheck(body) with
        | ExprLet(name, value, letBody, _parameters, _annotation, _requirements) ->
            match tailResumeArgument(value) with
                | Some(resumeArgument) ->
                    ExprTuple([resumeArgument, ExprLambda(name)(armBodyCapturePlaceholder(letBody))(None)])
                | None ->
                    ExprLet(name)(value)(armBodyCapturePlaceholder(letBody))([])(None)([])
        | ExprLetRecursive(name, value, letBody, _parameters, _annotation, _requirements) ->
            ExprLetRecursive(name)(value)(armBodyCapturePlaceholder(letBody))([])(None)([])
        | ExprIf(condition, thenBranch, elseBranch) ->
            elseBranch
            |> armBodyCapturePlaceholder
            |> ExprIf(condition)(armBodyCapturePlaceholder(thenBranch))
        | ExprMatch(value, cases, position) ->
            match tailResumeArgument(value) with
                | Some(resumeArgument) ->
                    ExprTuple(
                        [
                            resumeArgument,
                            ExprLambda("__resume_result_placeholder")(ExprMatch(ExprVar("__resume_result_placeholder"))(cases)(position))(None)
                        ]
                    )
                | None ->
                    ExprMatch(value)(armBodyCapturePlaceholderCases(cases))(position)
        | _ ->
            match tailResumeArgument(body) with
                | Some(resumedValue) -> resumedValue
                | None -> body
and armBodyCapturePlaceholderCases cases =
    match cases with
        | [] -> []
        | (pattern, body, guard) :: rest -> (pattern, armBodyCapturePlaceholder(body), guard) :: armBodyCapturePlaceholderCases(rest)

// The one-shot post-resume leaf: `v` is returned to the perform site immediately, and
// `given postName -> postBody` is lowered as a fresh closure and stashed in the pending-post
// register for collectCapabilityPost (at the perform call site) to pick up.
let lowerOneShotPost resumeArgument postName postBody lower postRegisterIndex state =
    match lower(resumeArgument)(state) with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = valueState, temp = valueTemp, semanticType = valueType, error = None } ->
            match lowerLambda(postName)(postBody)(None)(false)(lower)(valueState) with
                | LoweredCoreValue { state = failedPostState, error = Some(error) } -> failure(failedPostState)(error)
                | LoweredCoreValue { state = postState, temp = postTemp, error = None } ->
                    postState
                    |> emit(StoreCapabilityHandler(postRegisterIndex)(postTemp))
                    |> success(valueTemp)(valueType)

// Resolves an operation arm's body (after all of its own operation parameters have been peeled
// off by lowerOperationArmParameters) to its final lowered value: recurses through any
// non-resuming `let`/`let recursive` prefix — an ordinary binding evaluated before the arm
// resumes, e.g. `let y = f(x) in resume(y)` — through an `if` whose condition doesn't resume (each
// branch independently resolved, possibly to a different resume shape — tail in one arm, one-shot
// in the other), and through a `match` whose scrutinee and guards don't resume (same per-case
// independence as `if`'s branches), down to the eventual resume shape, bare tail `resume(e)` or
// one-shot `let x = resume(v) in body` or one-shot `match resume(v) with | ...`. Mirrors stage-0's
// TryRewriteResume family (TryRewriteResumeLet/LetRecursive/If/MatchCases/OneShotMatch), but
// interleaves shape recognition with real lowering rather than rewriting the Expr tree first and
// lowering the rewrite once: selfhost's Expr type is a closed ADT with no synthetic "post" node to
// rewrite into, so each non-resuming prefix's own value is lowered here, in place, through the
// ordinary let/let-recursive/if/match lowering path (finishLetValue /
// lowerPreparedRecursiveGroupWith / lowerIfThenBranch+finishIfElseBranch / a
// resolveOperationArmMatchArm(s) mirror of lowerMatchArm(s)), with the recursive call into the
// rest of the arm body supplied as the continuation — the same sentinel-placeholder-plus-custom-
// continuation technique lowerCoreProgramItems already uses for top-level declarations. `resume`
// in the `if`'s own condition, or a match's scrutinee/any guard, is rejected outright, same as
// stage-0's TryRewriteResumeIf/TryRewriteResumeMatchCases — there is no one-shot if-condition-
// resume shape, but a match scrutinee that IS directly a resume call is the distinct one-shot
// scrutinee shape (stage-0's TryRewriteResumeOneShotMatch): the whole match, re-run against the
// resumed value via a fresh synthetic parameter, becomes the single post continuation (reusing
// lowerOneShotPost unchanged), and every case's body/guard must not itself resume again
// (multi-shot rejected, matchCasesReferenceResume).
let recursive resolveOperationArmBody body lower postRegisterIndex capName opName armState =
    armState
    |> clearConsumerRequest
    |> resolveOperationArmBodyIn(body)(lower)(postRegisterIndex)(capName)(opName)
and resolveOperationArmBodyIn body lower postRegisterIndex capName opName state =
    match unspanForResumeCheck(body) with
        | ExprLet(name, value, letBody, _parameters, _annotation, _requirements) ->
            match tailResumeArgument(value) with
                | Some(resumeArgument) -> lowerOneShotPost(resumeArgument)(name)(letBody)(lower)(postRegisterIndex)(state)
                | None ->
                    if exprReferencesResume(value)
                    then
                        opName
                        |> UnsupportedOperationArmResume(capName)
                        |> failure(state)
                    else
                        match state with
                            | CoreLoweringState { bindings = outerBindings } ->
                                state
                                |> armSourceFunction(name)(value)(nameUsedOnlyAsDirectCallee(name)(letBody))
                                |> lower(value)
                                |> finishLetValue(
                                    name,
                                    topLevelContinuationBody,
                                    given (_ignoredBody) ->
                                        given (s) -> resolveOperationArmBody(letBody)(lower)(postRegisterIndex)(capName)(opName)(s),
                                    outerBindings
                                )
        | ExprLetRecursive(name, value, letBody, _parameters, _annotation, _requirements) ->
            if exprReferencesResume(value)
            then
                opName
                |> UnsupportedOperationArmResume(capName)
                |> failure(state)
            else
                match state with
                    | CoreLoweringState { bindings = outerBindings, nextLambdaId = lambdaId } ->
                        []
                        |> prepareRecursiveGroup([(name, value)])(state)
                        |> relabelSingleRecursive(lambdaId)
                        |> lowerPreparedRecursiveGroupWith(
                            [(name, value)],
                            topLevelContinuationBody,
                            lower,
                            given (_ignoredBody) ->
                                given (s) -> resolveOperationArmBody(letBody)(lower)(postRegisterIndex)(capName)(opName)(s),
                            outerBindings
                        )
        | ExprIf(condition, thenBranch, elseBranch) ->
            if exprReferencesResume(condition)
            then
                opName
                |> UnsupportedOperationArmResume(capName)
                |> failure(state)
            else
                let branchLower =
                    given (branchBody) ->
                        given (s) -> resolveOperationArmBody(branchBody)(lower)(postRegisterIndex)(capName)(opName)(s)
                in
                    state
                    |> lower(condition)
                    |> prepareIfPlan
                    |> lowerIfThenBranch(thenBranch)(emptyConsumerRequest)(branchLower)
                    |> finishIfElseBranch(elseBranch)(emptyConsumerRequest)(branchLower)
        // A scrutinee that IS itself a resume call (`match resume(v) with | ...`) is the one-shot
        // scrutinee shape (stage-0's TryRewriteResumeOneShotMatch): `v` returns to the perform
        // site immediately, and the WHOLE match — re-run against the resumed value, via a fresh
        // synthetic parameter — becomes the single post continuation, mirroring stage-0's
        // BuildCapabilityPost(postParam, new Match(postScrutinee, oneShotMatch.Cases, ...), ...).
        // Since the whole match is now the post body, there is no further per-case independent
        // resolution the way ordinary case-body recursion (below) allows — every case's body and
        // guard must NOT reference resume again (multi-shot rejected, matchCasesReferenceResume),
        // and the reconstructed match lowers through the arm's own ordinary `lower` inside
        // lowerOneShotPost (unchanged from the one-shot-let case — it already accepts an arbitrary
        // postBody Expr, and an ordinary ExprMatch needs no resolveOperationArmBody routing once
        // none of its cases resume).
        | ExprMatch(value, cases, position) ->
            match tailResumeArgument(value) with
                | Some(resumeArgument) ->
                    if matchCasesReferenceResume(cases)
                    then
                        opName
                        |> UnsupportedOperationArmResume(capName)
                        |> failure(state)
                    else
                        match freshTemp(state) with
                            | FreshTemp { state = namedState, temp = paramId } ->
                                let postParamName = "__resume_result_" + Ashes.Text.fromInt(paramId)
                                in
                                    let postBody = ExprMatch(ExprVar(postParamName))(cases)(position)
                                    in lowerOneShotPost(resumeArgument)(postParamName)(postBody)(lower)(postRegisterIndex)(namedState)
                | None ->
                    // Case-body recursion only (mirrors stage-0's TryRewriteResumeMatchCases): the
                    // scrutinee and every guard must not reference resume at all, and each case
                    // body is independently resolved by this same recursion, same as an if's two
                    // branches. Always dispatches through the plain linear arm-by-arm path
                    // (resolveOperationArmMatchArms), never lowerMatch's own tag-group dispatch
                    // optimization (lowerMatchArmsViaTagGroups) — correct but potentially slower
                    // IR for a resume-containing match on constructor patterns; acceptable since
                    // operation arms are not hot paths the way ordinary pattern matching is.
                    if exprReferencesResume(value)
                    then
                        opName
                        |> UnsupportedOperationArmResume(capName)
                        |> failure(state)
                    else
                        state
                        |> lower(value)
                        |> prepareMatchPlan
                        |> resolveOperationArmMatchArms(cases)(lower)(postRegisterIndex)(capName)(opName)
                        |> finishMatchPlan
        | _ ->
            match tailResumeArgument(body) with
                | Some(resumedValue) -> lower(resumedValue)(state)
                | None ->
                    opName
                    |> UnsupportedOperationArmResume(capName)
                    |> failure(state)
// Mirrors lowerMatchArm, except the guard (which must not reference resume, checked here) still
// lowers via the arm's own ordinary `lower`, while the arm body lowers via a wrapper back into
// resolveOperationArmBody — the same split lowerOperationArmParameters/resolveOperationArmBody
// already use elsewhere, just applied per pipe stage instead of via a single injected `lower`
// (lowerMatchGuard and finishMatchArm are separate pipe stages here, so each can be handed a
// different `lower` directly, with no need to distinguish them inside one closure).
and resolveOperationArmMatchArm pattern body guard lower postRegisterIndex capName opName failLabel (bracket: ArenaBracket) plan =
    match plan with
        | CoreMatchPlan { state = state, valueTemp = valueTemp, valueType = valueType, resultSlot = resultSlot, endLabel = endLabel, resultType = resultType } ->
            match state with
                | CoreLoweringState { bindings = outerBindings } ->
                    let guardRejected =
                        match guard with
                            | Some(guardExpr) -> exprReferencesResume(guardExpr)
                            | None -> false
                    in
                        if guardRejected
                        then
                            opName
                            |> UnsupportedOperationArmResume(capName)
                            |> failure(state)
                        else
                            let bodyLower =
                                given (armBody) ->
                                    given (s) -> resolveOperationArmBody(armBody)(lower)(postRegisterIndex)(capName)(opName)(s)
                            in
                                state
                                |> preparePattern(pattern)
                                |> lowerPattern(pattern)(valueTemp)(valueType)(failLabel)
                                |> finishPatternArm(body)(guard)(failLabel)(resultSlot)(endLabel)(resultType)(emptyConsumerRequest)(outerBindings)(bracket)(lower)(bodyLower)
// Each operation arm is bracketed like a linear arm of an ordinary match.
and resolveOperationArmMatchArms cases lower postRegisterIndex capName opName plan =
    match (cases, plan) with
        | (_cases, CoreMatchPlan { error = Some(_error) }) -> plan
        | ([], _) -> plan
        | ((pattern, body, guard) :: rest, CoreMatchPlan { state = state, noMatchLabel = noMatchLabel }) ->
            match matchFailLabel(rest)(noMatchLabel)(state) with
                | FreshLabel { state = failState, label = failLabel } ->
                    (plan with state = failState)
                    |> bracketMatchArm(resolveOperationArmMatchArm(pattern)(body)(guard)(lower)(postRegisterIndex)(capName)(opName))(failLabel)
                    |> labelNextMatchArm(rest)(failLabel)
                    |> resolveOperationArmMatchArms(rest)(lower)(postRegisterIndex)(capName)(opName)

// Wraps an operation arm's (already resume-rewritten) body in one lambda per parameter, matching
// each non-variable pattern via a fresh synthetic parameter name — the ordinary lambda/match
// lowering already handles the result like any other closure. Position-based names are safe here
// because each arm's parameters are wrapped in one call, never interleaved with another arm's.
let recursive buildArmParameterExpr index patterns body =
    match patterns with
        | [] -> body
        | PatternVar(name) :: rest ->
            ExprLambda(name)(buildArmParameterExpr(index + 1)(rest)(body))(None)
        | pattern :: rest ->
            let paramName = "__arm_arg_" + Ashes.Text.fromInt(index)
            in
                let innerBody = buildArmParameterExpr(index + 1)(rest)(body)
                in
                    ExprLambda(paramName)(ExprMatch(ExprVar(paramName))([(pattern, innerBody, None)])(None))(None)

// Lowers an operation arm's non-tail-resume parameters directly through lowerLambda, one at a
// time, rather than building a plain Expr tree and handing it to `lower` in one call: lowerCore's
// own ExprLambda case always recurses into a nested lambda's body via itself (never an injected
// `lower`), so nothing below the outermost layer could be intercepted that way. lowerLambda has no
// such hard-coded recursion — it always lowers the body through whatever `lower` it's given — so
// calling it directly at every parameter layer, with the innermost layer resolving the arm's
// actual body (resolveOperationArmBody, which may itself recurse through non-resuming let
// prefixes before reaching a resume shape), reaches the same place. Every layer is handed the same
// placeholderBody purely so lowerLambda's own free-variable/capture analysis (`collectFree`) sees
// the closure's true free variables; the placeholder itself is never lowered.
let recursive lowerOperationArmParameters patterns armBody placeholderBody lower postRegisterIndex capName opName state =
    match patterns with
        | [] -> resolveOperationArmBody(armBody)(lower)(postRegisterIndex)(capName)(opName)(state)
        | PatternVar(name) :: rest ->
            lowerLambda(
                name,
                placeholderBody,
                None,
                false,
                given (_ignoredBody) ->
                    given (s) -> lowerOperationArmParameters(rest)(armBody)(placeholderBody)(lower)(postRegisterIndex)(capName)(opName)(s),
                state
            )
        | _pattern :: _rest ->
            opName
            |> UnsupportedOperationArmResume(capName)
            |> failure(state)

// A bare tail `resume(e)` (the arm's whole unwrapped body, no let/letrec prefix) reuses the plain
// closure/match lowering machinery via buildArmParameterExpr, which — unlike
// lowerOperationArmParameters — also supports non-variable operation parameters (synthetic name +
// match), since `e` needs no lowering-time hook of its own. Everything else (a one-shot
// `let x = resume(v) in body`, or any non-resuming let/letrec prefix before either shape) goes
// through the general resolveOperationArmBody recursion, which only supports variable operation
// parameters.
let buildOperationArmClosure capName opName patterns armBody lower postRegisterIndex state =
    match tailResumeArgument(armBody) with
        | Some(resumedValue) ->
            lower(buildArmParameterExpr(0)(patterns)(resumedValue))(state)
        | None ->
            let placeholderBody = armBodyCapturePlaceholder(armBody)
            in lowerOperationArmParameters(patterns)(armBody)(placeholderBody)(lower)(postRegisterIndex)(capName)(opName)(state)

// Lowers every operation arm of the single handled capability to a closure and stores each one
// into the handler frame at the same offset emitDynamicPerform reads from
// ((globalCount + 1 + opIndex) * 8), so a perform inside the handled body finds a real closure
// instead of the frame's uninitialized allocation.
let recursive installOperationArmClosures capName ops opArms frameTemp globalCount lower state =
    match opArms with
        | [] -> success(-1)(SemNever)(state)
        | (armCapName, opName, patterns, armBody) :: rest ->
            if armCapName != capName
            then installOperationArmClosures(capName)(ops)(rest)(frameTemp)(globalCount)(lower)(state)
            else
                match findCapabilityOperationIndex(opName)(ops) with
                    | None -> installOperationArmClosures(capName)(ops)(rest)(frameTemp)(globalCount)(lower)(state)
                    | Some(opIndex) ->
                        match buildOperationArmClosure(capName)(opName)(patterns)(armBody)(lower)(globalCount)(state) with
                            | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                            | LoweredCoreValue { state = closureState, temp = closureTemp, error = None } ->
                                closureState
                                |> emit(StoreMemOffset(frameTemp)((globalCount + 1 + opIndex) * 8)(closureTemp))
                                |> installOperationArmClosures(capName)(ops)(rest)(frameTemp)(globalCount)(lower)

// Folds every collected one-shot post continuation over the handle's current result, in
// most-recently-collected-first order (the posts list is a stack, newest at the head — matching
// the innermost `resume` completing first). Empty list: the loop's own JumpIfFalse skips straight
// to reading the initial result back out, so a handle with no one-shot resumes pays one dead
// compare-and-branch and nothing else. A real loop (labels/locals, not a pure Expr tree) because
// the list length is only known at runtime.
let recursive foldCapabilityPosts postsHeadPtrTemp initialResultTemp resultType state =
    match freshLocal(state) with
        | FreshLocal { state = resultLocalState, local = resultLocal } ->
            match resultLocalState
            |> emit(StoreLocal(resultLocal)(initialResultTemp))
            |> freshLocal with
                | FreshLocal { state = cellLocalState, local = cellLocal } ->
                    match freshTemp(cellLocalState) with
                        | FreshTemp { state = initHeadState, temp = initHeadTemp } ->
                            let initState =
                                initHeadState
                                |> emit(LoadMemOffset(initHeadTemp)(postsHeadPtrTemp)(0))
                                |> emit(StoreLocal(cellLocal)(initHeadTemp))
                            in
                                match freshLabel("capability_posts_loop")(initState) with
                                    | FreshLabel { state = loopLabelState, label = loopLabel } ->
                                        match freshLabel("capability_posts_done")(loopLabelState) with
                                            | FreshLabel { state = doneLabelState, label = doneLabel } ->
                                                doneLabelState
                                                |> emit(Label(loopLabel))
                                                |> foldCapabilityPostsLoop(loopLabel)(doneLabel)(cellLocal)(resultLocal)(resultType)
and foldCapabilityPostsLoop loopLabel doneLabel cellLocal resultLocal resultType state =
    match freshTemp(state) with
        | FreshTemp { state = cellState, temp = cellTemp } ->
            match cellState
            |> emit(LoadLocal(cellTemp)(cellLocal))
            |> freshTemp with
                | FreshTemp { state = zeroState, temp = zeroTemp } ->
                    match zeroState
                    |> emit(LoadConstInt(zeroTemp)(0))
                    |> freshTemp with
                        | FreshTemp { state = cmpState, temp = hasCellTemp } ->
                            let checkedState =
                                cmpState
                                |> emit(CmpIntNe(hasCellTemp)(cellTemp)(zeroTemp))
                                |> emit(JumpIfFalse(hasCellTemp)(doneLabel))
                            in
                                match freshTemp(checkedState) with
                                    | FreshTemp { state = closureState, temp = postClosureTemp } ->
                                        let loadedClosureState =
                                            emit(LoadMemOffset(postClosureTemp)(cellTemp)(0))(closureState)
                                        in
                                            match freshTemp(loadedClosureState) with
                                                | FreshTemp { state = currentResultState, temp = currentResultTemp } ->
                                                    let loadedResultState =
                                                        emit(LoadLocal(currentResultTemp)(resultLocal))(currentResultState)
                                                    in
                                                        match freshTemp(loadedResultState) with
                                                            | FreshTemp { state = callState, temp = nextResultTemp } ->
                                                                let calledState =
                                                                    callState
                                                                    |> emit(CallClosure(nextResultTemp)(postClosureTemp)(currentResultTemp)(-1))
                                                                    |> emit(StoreLocal(resultLocal)(nextResultTemp))
                                                                in
                                                                    match freshTemp(calledState) with
                                                                        | FreshTemp { state = nextCellState, temp = nextCellTemp } ->
                                                                            nextCellState
                                                                            |> emit(LoadMemOffset(nextCellTemp)(cellTemp)(8))
                                                                            |> emit(StoreLocal(cellLocal)(nextCellTemp))
                                                                            |> emit(Jump(loopLabel))
                                                                            |> emit(Label(doneLabel))
                                                                            |> finishCapabilityPostsFold(resultLocal)(resultType)
and finishCapabilityPostsFold resultLocal resultType state =
    match freshTemp(state) with
        | FreshTemp { state = readState, temp = finalResultTemp } ->
            readState
            |> emit(LoadLocal(finalResultTemp)(resultLocal))
            |> success(finalResultTemp)(resultType)

let lowerHandleWithExpected body arms (request: ConsumerRequest) lower state =
    match splitHandlerArms(arms) with
        | ParsedHandlerArms { opArms = opArms, returnArm = returnArm } ->
            match state with
                | CoreLoweringState { capabilityLayouts = capLayouts, capabilityGlobalCount = globalCount, bindings = outerBindings } ->
                    match freshTemp(state) with
                        | FreshTemp { state = stackState, temp = postsHeadPtrTemp } ->
                            let initPostsState =
                                emit(AllocStack(postsHeadPtrTemp)(8))(stackState)
                            in
                                match freshTemp(initPostsState) with
                                    | FreshTemp { state = zeroState, temp = zeroTemp } ->
                                        let prepState =
                                            zeroState
                                            |> emit(LoadConstInt(zeroTemp)(0))
                                            |> emit(StoreMemOffset(postsHeadPtrTemp)(0)(zeroTemp))
                                        in
                                            match opArms with
                                                | [] ->
                                                    prepState
                                                    |> withConsumerRequest(request)
                                                    |> lower(body)
                                                | (capName, _opName, _pats, _armBody) :: _ ->
                                                    match findCapabilityLayout(capName)(capLayouts) with
                                                        | None ->
                                                            prepState
                                                            |> withConsumerRequest(request)
                                                            |> lower(body)
                                                        | Some(CoreCapabilityLayout { index = capIdx, operations = ops }) ->
                                                            let opCount = coreListLength(ops)
                                                            in
                                                                match freshTemp(prepState) with
                                                                    | FreshTemp { state = frameAllocState, temp = frameTemp } ->
                                                                        let frameSize = (globalCount + 1 + opCount) * 8
                                                                        in
                                                                            let frameInitState =
                                                                                frameAllocState
                                                                                |> emit(AllocStack(frameTemp)(frameSize))
                                                                                |> emitSnapshotGlobals(0)(globalCount)(frameTemp)
                                                                                |> emit(StoreMemOffset(frameTemp)(globalCount * 8)(postsHeadPtrTemp))
                                                                                |> emit(StoreCapabilityHandler(capIdx)(frameTemp))
                                                                            in
                                                                                match installOperationArmClosures(capName)(ops)(opArms)(frameTemp)(globalCount)(lower)(frameInitState) with
                                                                                    | LoweredCoreValue { state = _armsFailedState, error = Some(error) } -> failure(frameInitState)(error)
                                                                                    | LoweredCoreValue { state = armsInstalledState, error = None } ->
                                                                                        match armsInstalledState
                                                                                        |> withConsumerRequest(request)
                                                                                        |> lower(body) with
                                                                                            | LoweredCoreValue { state = bodyState, temp = bodyTemp, semanticType = bodyType, error = None } ->
                                                                                                let uninstallState =
                                                                                                    match freshTemp(bodyState) with
                                                                                                        | FreshTemp { state = unState, temp = prevTemp } ->
                                                                                                            unState
                                                                                                            |> emit(LoadMemOffset(prevTemp)(frameTemp)(capIdx * 8))
                                                                                                            |> emit(StoreCapabilityHandler(capIdx)(prevTemp))
                                                                                                in
                                                                                                    let currentResultOutcome =
                                                                                                        match returnArm with
                                                                                                            | None -> success(bodyTemp)(bodyType)(uninstallState)
                                                                                                            | Some((returnPat, returnExpr)) ->
                                                                                                                finishLetValue(
                                                                                                                    "__body_res",
                                                                                                                    ExprMatch(ExprVar("__body_res"))([(returnPat, returnExpr, None)])(None),
                                                                                                                    lower,
                                                                                                                    outerBindings,
                                                                                                                    success(bodyTemp)(bodyType)(uninstallState)
                                                                                                                )
                                                                                                    in
                                                                                                        match currentResultOutcome with
                                                                                                            | LoweredCoreValue { state = failedResultState, error = Some(error) } -> failure(failedResultState)(error)
                                                                                                            | LoweredCoreValue { state = resultState, temp = resultTemp, semanticType = resultType, error = None } -> foldCapabilityPosts(postsHeadPtrTemp)(resultTemp)(resultType)(resultState)
                                                                                            | failed -> failed

// The handled body inherits the context's expected type; the operation arms do not.
let lowerHandle body arms lower state =
    state
    |> clearConsumerRequest
    |> lowerHandleWithExpected(body)(arms)(state
    |> consumerRequestOf
    |> branchRequest)(lower)

let expressionName expression =
    match expression with
        | ExprBigInt(_) -> "BigInt"
        | ExprQualifiedVar(_, _) -> "qualified variable"
        | _ -> "non-core expression"

// A constructor call's arguments are lowered without the expected type and its result unified
// with it; a builtin or external call ignores it; a general call constrains its result with it
// and lowers each argument against the callee's parameter type.
let unifyOptionalExpectedResult expected lowered =
    match expected with
        | None -> lowered
        | Some(expectedType) -> unifyExpectedResult(expectedType)(lowered)

let lowerCallExpression expression function argument lower state =
    match state
    |> clearConsumerRequest
    |> tryLowerConstructorCall(expression)(lower) with
        | Some(lowered) ->
            unifyOptionalExpectedResult(expectedTypeOf(state))(lowered)
        | None ->
            match tryLowerBuiltinCall(expression)(lower)(withConsumerRequest((emptyConsumerRequest with runtimeString = runtimeStringRequested(state)))(state)) with
                | Some(lowered) -> lowered
                | None ->
                    match state
                    |> clearConsumerRequest
                    |> tryLowerExternalCall(expression)(lower) with
                        | Some(lowered) -> lowered
                        | None ->
                            state
                            |> clearConsumerRequest
                            |> lowerCall(collectCallSpine(expression))(function)(argument)(expectedTypeOf(state))(lower)
                            |> markLoweredCallArgumentsMoved(collectCallSpine(expression))

let lowerCoreDispatch expression lowerCore state =
    match expression with
        | ExprAt(span, inner) ->
            match state with
                | CoreLoweringState { currentSpan = previous } ->
                    match lowerCore(inner)((state with currentSpan = Some(span))) with
                        | LoweredCoreValue { state = innerState, temp = temp, semanticType = semanticType, error = error } ->
                            let restored = innerState with currentSpan = previous
                            in
                                LoweredCoreValue(
                                    state = restored,
                                    temp = temp,
                                    semanticType = semanticType,
                                    error = error
                                )
        | ExprInt(value) ->
            lowerConstant(given (target) -> LoadConstInt(target)(value))(SemInt)(state)
        | ExprBigInt(digits) -> lowerCoreBigInt(digits)(state)
        | ExprUInt(value, bits, _raw) ->
            lowerConstant(given (target) -> LoadConstInt(target)(value))(SemUInt(bits))(state)
        | ExprFloat(value, _raw) ->
            lowerConstant(given (target) -> LoadConstFloat(target)(value))(SemFloat)(state)
        | ExprString(value) -> lowerString(value)(state)
        | ExprRune(value) ->
            lowerConstant(given (target) -> LoadConstInt(target)(value))(SemRune)(state)
        | ExprBool(value) ->
            lowerConstant(given (target) -> LoadConstBool(target)(value))(SemBool)(state)
        | ExprAdd(left, right) -> lowerCoreBinary(CoreAddOperator)(left)(right)(lowerCore)(state)
        | ExprSubtract(left, right) -> lowerCoreBinary(CoreSubtractOperator)(left)(right)(lowerCore)(state)
        | ExprMultiply(left, right) -> lowerCoreBinary(CoreMultiplyOperator)(left)(right)(lowerCore)(state)
        | ExprDivide(left, right) -> lowerCoreBinary(CoreDivideOperator)(left)(right)(lowerCore)(state)
        | ExprModulo(left, right) -> lowerCoreBinary(CoreModuloOperator)(left)(right)(lowerCore)(state)
        | ExprBitwiseAnd(left, right) -> lowerCoreBinary(CoreBitwiseAndOperator)(left)(right)(lowerCore)(state)
        | ExprBitwiseOr(left, right) -> lowerCoreBinary(CoreBitwiseOrOperator)(left)(right)(lowerCore)(state)
        | ExprBitwiseXor(left, right) -> lowerCoreBinary(CoreBitwiseXorOperator)(left)(right)(lowerCore)(state)
        | ExprShiftLeft(left, right) -> lowerCoreBinary(CoreShiftLeftOperator)(left)(right)(lowerCore)(state)
        | ExprShiftRight(left, right) -> lowerCoreBinary(CoreShiftRightOperator)(left)(right)(lowerCore)(state)
        | ExprBitwiseNot(operand) -> lowerCoreBitwiseNot(operand)(lowerCore)(state)
        | ExprLogicalNot(operand) -> lowerCoreLogicalNot(operand)(lowerCore)(state)
        | ExprLogicalAnd(left, right) -> lowerIf(left)(right)(ExprBool(false))(lowerCore)(state)
        | ExprLogicalOr(left, right) -> lowerIf(left)(ExprBool(true))(right)(lowerCore)(state)
        | ExprGreaterThan(left, right) -> lowerCoreBinary(CoreGreaterOperator)(left)(right)(lowerCore)(state)
        | ExprGreaterOrEqual(left, right) -> lowerCoreBinary(CoreGreaterOrEqualOperator)(left)(right)(lowerCore)(state)
        | ExprLessThan(left, right) -> lowerCoreBinary(CoreLessOperator)(left)(right)(lowerCore)(state)
        | ExprLessOrEqual(left, right) -> lowerCoreBinary(CoreLessOrEqualOperator)(left)(right)(lowerCore)(state)
        | ExprEqual(left, right) -> lowerCoreBinary(CoreEqualOperator)(left)(right)(lowerCore)(state)
        | ExprNotEqual(left, right) -> lowerCoreBinary(CoreNotEqualOperator)(left)(right)(lowerCore)(state)
        | ExprVar(name) -> lowerCoreVariable(name)(lowerCore)(state)
        | ExprQualifiedVar(receiverName, fieldName) -> lowerQualified(receiverName)(fieldName)(lowerCore)(state)
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) ->
            lowerLet(
                name,
                value,
                body,
                lowerCore,
                state
            )
        | ExprLetRecursive(name, value, body, _parameters, _annotation, _requirements) ->
            lowerLetRecursive(
                name,
                value,
                body,
                lowerCore,
                state
            )
        | ExprIf(condition, thenBranch, elseBranch) -> lowerIf(condition)(thenBranch)(elseBranch)(lowerCore)(state)
        | ExprLambda(parameter, body, annotation) -> lowerLambda(parameter)(body)(annotation)(state.pendingStackClosure)(lowerCore)(state)
        | ExprCall(function, argument, _whitespace, _layout) -> lowerCallExpression(expression)(function)(argument)(lowerCore)(state)
        | ExprTuple(elements) -> lowerTuple(elements)(lowerCore)(state)
        | ExprList(elements, _isMultiline) -> lowerListLiteral(elements)(lowerCore)(state)
        | ExprCons(head, tail) -> lowerCons(head)(tail)(lowerCore)(state)
        | ExprRecord(name, fields, _isMultiline) -> lowerRecord(name)(fields)(lowerCore)(state)
        | ExprRecordUpdate(target, fields) -> lowerRecordUpdate(target)(fields)(lowerCore)(state)
        | ExprMatch(value, cases, _position) -> lowerMatch(value)(cases)(lowerCore)(state)
        | ExprPerform(operation) -> lowerPerform(operation)(lowerCore)(state)
        | ExprHandle(body, arms) -> lowerHandle(body)(arms)(lowerCore)(state)
        | unsupported ->
            failure(state)(unsupported
            |> expressionName
            |> UnsupportedCoreLoweringExpression)

// The context's request reaches the dispatch trimmed to what this expression kind forwards; an
// expected type a kind does not forward is unified with its result afterwards. The result state
// never carries a request.
let recursive lowerCore expression state =
    match consumerRequestOf(state) with
        | request ->
            match dispatchRequest(expression)(request) with
                | dispatched ->
                    state
                    |> withConsumerRequest(dispatched)
                    |> lowerCoreDispatch(expression)(lowerCore)
                    |> withLoweredConsumerRequest(emptyConsumerRequest)
                    |> unifyUnforwardedExpectedType(expression)(request.expectedType)

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
        error = Some(error)
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

type TopLevelDuplicateCheck =
    | seen: List(Str)
    | duplicate: Maybe(Str)

let recursive checkTopLevelNames names seen =
    match names with
        | [] -> TopLevelDuplicateCheck(seen = seen, duplicate = None)
        | name :: rest ->
            if containsName(name)(seen)
            then TopLevelDuplicateCheck(seen = seen, duplicate = Some(name))
            else checkTopLevelNames(rest)(name :: seen)

let recursive letBindingSyntaxPairs bindings =
    match bindings with
        | [] -> []
        | LetBindingSyntax { name = name, value = value } :: rest -> (name, value) :: letBindingSyntaxPairs(rest)

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
let recursive allTopLevelBindingNames items =
    match items with
        | [] -> []
        | TopLevelAt(_span, inner) :: rest -> allTopLevelBindingNames(inner :: rest)
        | TopLevelLet(LetBindingSyntax { name = name }, _isRecursive) :: rest -> name :: allTopLevelBindingNames(rest)
        | TopLevelRecursiveGroup(bindings) :: rest ->
            rest
            |> allTopLevelBindingNames
            |> append(letBindingSyntaxNames(bindings))
        | _ :: rest -> allTopLevelBindingNames(rest)

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
// (see docs/md/future/SELF_HOSTING.md). Takes the sentinel-placeholder continuation
// lowerCoreProgramItems supplies (see its own TopLevelLet case) in place of a literal body Expr.
let lowerArenaBracketedTopLevelLet name value environment continuation outerBindings stackClosure state =
    match openArenaBracket(state) with
        | ArenaBracket { bracketState = saved, bracketCursorSlot = cursorSlot, bracketEndSlot = endSlot } ->
            match rewriteTraitConstrainedTopLevelValue(name)(value)(environment) with
                | TraitConstrainedTopLevelValueRewriting { value = _rewrittenValue, error = Some(error) } -> failure(saved)(UnresolvedTraitEvidenceForwarding(error))
                | TraitConstrainedTopLevelValueRewriting { value = rewrittenValue, error = None } ->
                    match saved
                    |> armSourceFunction(name)(rewrittenValue)(stackClosure)
                    |> lowerCore(rewrittenValue) with
                        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                        | LoweredCoreValue { error = None } as loweredValue ->
                            match finishLetValueInSlot(name)(topLevelContinuationBody)(continuation)(outerBindings)(loweredValue) with
                                | (LoweredCoreValue { state = failedState, error = Some(error) }, _slot) -> failure(failedState)(error)
                                | (LoweredCoreValue { state = bodyState, temp = resultTemp, semanticType = resultType, error = None }, ownerSlot) ->
                                    match closeOwnedLetBracket(loweredValueOwnedTypeName(loweredValue))(ownerSlot)(cursorSlot)(endSlot)(resultTemp)(resultType)(bodyState) with
                                        | (closed, finalTemp) -> success(finalTemp)(resultType)(closed)

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

let recursive letBindingSyntaxValuesHaveNonCalleeUse (name: Str) (bindings: List(LetBindingSyntax)) =
    match bindings with
        | [] -> false
        | LetBindingSyntax { value = value } :: rest -> nameHasNonCalleeUse(name)(value)(false) || letBindingSyntaxValuesHaveNonCalleeUse(name)(rest)

// The direct-callee analysis over the flat top-level sequence after a binding, the scope stage 0
// walks as the desugared nested `let` body: a later value or the trailing expression that uses
// `name` other than as a callee keeps its closure on the heap, and a later binding of the same
// name shadows it and ends the walk.
let recursive topLevelNameUsedOnlyAsDirectCallee (name: Str) (items: List(TopLevelItem)) (trailingBody: Expr) =
    match items with
        | [] -> nameUsedOnlyAsDirectCallee(name)(trailingBody)
        | TopLevelAt(_span, inner) :: rest -> topLevelNameUsedOnlyAsDirectCallee(name)(inner :: rest)(trailingBody)
        | TopLevelLet(LetBindingSyntax { name = bound, value = value }, isRecursive) :: rest ->
            if isRecursive && bound == name
            then true
            else
                if nameHasNonCalleeUse(name)(value)(false)
                then false
                else
                    if bound == name
                    then true
                    else topLevelNameUsedOnlyAsDirectCallee(name)(rest)(trailingBody)
        | TopLevelRecursiveGroup(bindings) :: rest ->
            if bindings
            |> letBindingSyntaxNames
            |> containsName(name)
            then true
            else
                if letBindingSyntaxValuesHaveNonCalleeUse(name)(bindings)
                then false
                else topLevelNameUsedOnlyAsDirectCallee(name)(rest)(trailingBody)
        | _other :: rest -> topLevelNameUsedOnlyAsDirectCallee(name)(rest)(trailingBody)

// Recognizes ONLY `Ctor(arg)` — one, fully-saturating argument — against a known constructor whose
// scheme is exactly `a -> T(...)` (not itself a function, ruling out a curried/multi-argument
// constructor this slice does not attempt) and that isn't zero-cost (a zero-cost constructor never
// reaches `finishConstructorAllocation`'s `AllocAdt` path at all, so there is nothing to drop).
// Every constructor `standardConstructorLayouts` registers today (`Some`, `Ok`, `Error`) is exactly
// this shape. A partial application of a real multi-argument constructor would also syntactically
// match `ExprCall(ExprVar(ctorName), _, _, _)` here, which is why the scheme's OWN arity is checked
// against — not just that the call produces one argument — before ever answering `Some`.
let directSingleArgRcConstructorLayout (expr: Expr) (constructorLayouts: List(CoreConstructorLayout)) =
    match stripExprAt(expr) with
        | ExprCall(callee, _arg, _isSugar, _argLayout) ->
            match stripExprAt(callee) with
                | ExprVar(constructorName) ->
                    match findConstructorLayout(constructorName)(constructorLayouts) with
                        | Some(CoreConstructorLayout { isZeroCost = true }) -> None
                        | Some(CoreConstructorLayout { scheme = TypeScheme { body = SemFunction(_parameterType, resultType, _effect) } } as layout) ->
                            match resultType with
                                | SemFunction(_, _, _) -> None
                                | _ -> Some(layout)
                        | _ -> None
                | _ -> None
        | _ -> None

// Names the structural release helper for a value of `semanticType` (stage 0's
// `SynthesizeStructuralOwnerDropper`), synthesizing it and any ADT dropper it calls into the
// program once per type through the state's label cache; `None` when the value's release is a
// single allocation.
let synthesizeStructuralDropperLabel (semanticType: SemanticType) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { constructorLayouts = layouts, dropperLabels = cache, functions = functions, nextLambdaId = lambdaId, nextLabelId = labelId } ->
            match synthesizeStructuralOwnerDropper(resolveType(state)(semanticType))(constructorInferenceDefinitionsFromLayouts(layouts))(cache)(lambdaId)(labelId) with
                | DropperSynthesis { label = label, cache = nextCache, functions = synthesized, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId } -> (label, (state with dropperLabels = nextCache, functions = append(functions)(synthesized), nextLambdaId = nextLambdaId, nextLabelId = nextLabelId))

// Called only once the caller has confirmed `value` is a direct, fully-saturating call to a
// field-carrying constructor and `name` is provably dead. Skips `finishLetValue`'s local-slot/
// binding machinery entirely (nothing will ever read `name` back): lowers the value, immediately
// releases it with a single `RcDrop` (`ownerSlot = -1`, since this value is never stored to a
// local) naming the type's structural dropper when the release reaches past the cell, then
// continues lowering the rest of the program.
let lowerDeadRcTopLevelLet name value layout environment continuation state =
    match rewriteTraitConstrainedTopLevelValue(name)(value)(environment) with
        | TraitConstrainedTopLevelValueRewriting { value = _rewrittenValue, error = Some(error) } -> failure(state)(UnresolvedTraitEvidenceForwarding(error))
        | TraitConstrainedTopLevelValueRewriting { value = rewrittenValue, error = None } ->
            match lowerCore(rewrittenValue)((state with runtimeAdtRequested = true)) with
                | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
                | LoweredCoreValue { state = valueState, temp = valueTemp, semanticType = valueType, error = None } ->
                    match (layout, synthesizeStructuralDropperLabel(valueType)((valueState with runtimeAdtRequested = false))) with
                        | (CoreConstructorLayout { name = constructorName }, (dropperLabel, dropperState)) ->
                            dropperState
                            |> emit(RcDrop(valueTemp)(constructorName)(-1)(true)(false)(dropperLabel))
                            |> continuation(topLevelContinuationBody)

let recursive constructorFieldSemanticTypes (parameters: List(TypeExpr)) (parameterTypes: List((Str, SemanticType))) =
    match parameters with
        | [] -> Some([])
        | parameter :: rest ->
            match typeExprToSemanticType(parameter)(parameterTypes) with
                | None -> None
                | Some(fieldType) ->
                    match constructorFieldSemanticTypes(rest)(parameterTypes) with
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
        | _other -> None
and typeExprArityErrorsList (typeExprs: List(TypeExpr)) (layouts: List(CoreConstructorLayout)) =
    match typeExprs with
        | [] -> None
        | head :: tail ->
            match typeExprArityErrors(head)(layouts) with
                | Some(mismatch) -> Some(mismatch)
                | None -> typeExprArityErrorsList(tail)(layouts)

let arityMismatchMessage name expected actual = "Type '" + name + "' expects " + Ashes.Text.fromInt(expected) + " type argument(s) but got " + Ashes.Text.fromInt(actual) + "."

let buildUserConstructorLayout (resultType: SemanticType) (quantified: List((Int, Str))) (parameterTypes: List((Str, SemanticType))) (tag: Int) (layouts: List(CoreConstructorLayout)) (constructor: TypeConstructor) =
    match constructor with
        | TypeConstructor { name = name, parameters = parameters, fieldNames = fieldNames } ->
            match typeExprArityErrorsList(parameters)(layouts) with
                | Some((typeName, expected, actual)) ->
                    Error(actual
                    |> arityMismatchMessage(typeName)(expected)
                    |> UnsupportedTypeDeclaration)
                | None ->
                    match constructorFieldSemanticTypes(parameters)(parameterTypes) with
                        | None -> Error(UnsupportedTypeDeclaration("constructor '" + name + "' has a field type outside the supported scalar/type-parameter set (Int, Str, Bool, Float, BigInt, Rune, Bytes, Unit, or one of the type's own type parameters)"))
                        | Some(fieldTypes) ->
                            Ok(CoreConstructorLayout(
                                name = name,
                                tag = tag,
                                scheme = TypeScheme(quantified = quantified, body = buildConstructorSchemeBody(fieldTypes)(resultType), constraints = []),
                                fieldNames = fieldNames,
                                isZeroCost = false,
                                tagless = false
                            ))

let recursive buildUserConstructorLayoutsFromIndex (resultType: SemanticType) (quantified: List((Int, Str))) (parameterTypes: List((Str, SemanticType))) (index: Int) (layouts: List(CoreConstructorLayout)) (constructors: List(TypeConstructor)) =
    match constructors with
        | [] -> Ok([])
        | constructor :: rest ->
            match buildUserConstructorLayout(resultType)(quantified)(parameterTypes)(index)(layouts)(constructor) with
                | Error(error) -> Error(error)
                | Ok(layout) ->
                    match buildUserConstructorLayoutsFromIndex(resultType)(quantified)(parameterTypes)(index + 1)(layouts)(rest) with
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
// `state.constructorLayouts` — the same list `standardConstructorLayouts` seeds intrinsically, read
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

let isKnownTypeName name selfName layouts externalOpaqueTypes =
    if name == selfName
    then true
    else
        if isPrimitiveTypeName(name) || isBuiltinRuntimeTypeName(name)
        then true
        else
            if containsTypeName(externalOpaqueTypes)(name)
            then true
            else
                match findDeclaredTypeArity(name)(layouts) with
                    | Some(_arity) -> true
                    | None -> false

let recursive collectImplicitTypeParameterNames (typeExpr: TypeExpr) selfName layouts externalOpaqueTypes acc =
    match typeExpr with
        | TypeAt(_span, inner) -> collectImplicitTypeParameterNames(inner)(selfName)(layouts)(externalOpaqueTypes)(acc)
        | TypeNamed(name) ->
            if isKnownTypeName(name)(selfName)(layouts)(externalOpaqueTypes)
            then acc
            else
                if containsTypeName(acc)(name)
                then acc
                else append(acc)([name])
        | TypeApplied("List", element :: []) -> collectImplicitTypeParameterNames(element)(selfName)(layouts)(externalOpaqueTypes)(acc)
        | TypeApplied(_name, arguments) -> collectImplicitTypeParameterNamesList(arguments)(selfName)(layouts)(externalOpaqueTypes)(acc)
        | TypeTuple(elements) -> collectImplicitTypeParameterNamesList(elements)(selfName)(layouts)(externalOpaqueTypes)(acc)
        | _other -> acc
and collectImplicitTypeParameterNamesList (typeExprs: List(TypeExpr)) selfName layouts externalOpaqueTypes acc =
    match typeExprs with
        | [] -> acc
        | head :: tail ->
            acc
            |> collectImplicitTypeParameterNames(head)(selfName)(layouts)(externalOpaqueTypes)
            |> collectImplicitTypeParameterNamesList(tail)(selfName)(layouts)(externalOpaqueTypes)

let recursive collectImplicitTypeParametersFromConstructors (constructors: List(TypeConstructor)) selfName layouts externalOpaqueTypes acc =
    match constructors with
        | [] -> acc
        | TypeConstructor { parameters = parameters } :: rest ->
            acc
            |> collectImplicitTypeParameterNamesList(parameters)(selfName)(layouts)(externalOpaqueTypes)
            |> collectImplicitTypeParametersFromConstructors(rest)(selfName)(layouts)(externalOpaqueTypes)

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

let recursive markTaglessLayouts (state: CoreLoweringState) (schemes: List(TypeScheme)) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | (CoreConstructorLayout { scheme = scheme, isZeroCost = isZeroCost } as layout) :: rest ->
            (layout with tagless = isTaglessAdtConstructor(given (name: Str) -> isDeclaredResourceName(name)(state))(schemes)(isZeroCost)(scheme)) :: markTaglessLayouts(state)(schemes)(rest)

// The layouts of one type declaration with their tagless flag decided (see TaglessAdtLayout). The
// sibling count and the resource walk see every registered constructor scheme, the declaration's
// own included, so a self-referential field and an earlier resource-bearing type are both visible.
let decideTaglessLayouts (state: CoreLoweringState) (existingLayouts: List(CoreConstructorLayout)) (newLayouts: List(CoreConstructorLayout)) =
    markTaglessLayouts(state)(newLayouts
    |> append(existingLayouts)
    |> layoutSchemes)(newLayouts)

let registerTopLevelTypeDeclaration (declaration: TypeDecl) (state: CoreLoweringState) =
    match declaration with
        | TypeDecl { name = name, typeParameters = typeParameters, constructors = constructors } ->
            if isReservedTypeName(name)
            then Error(ReservedTypeName("'Ashes' and built-in runtime types are reserved"))
            else
                match state with
                    | CoreLoweringState { typeSupply = supply, constructorLayouts = existingLayouts, externalOpaqueTypes = externalOpaqueTypes } ->
                        let effectiveTypeParameters =
                            match typeParameters with
                                | [] ->
                                    []
                                    |> collectImplicitTypeParametersFromConstructors(constructors)(name)(existingLayouts)(externalOpaqueTypes)
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
                                                match buildUserConstructorLayoutsFromIndex(resultType)(quantified)(parameterTypes)(0)(existingLayouts)(constructors) with
                                                    | Error(error) -> Error(error)
                                                    | Ok(newLayouts) ->
                                                        Ok((state with constructorLayouts = append(existingLayouts)(decideTaglessLayouts(state)(existingLayouts)(newLayouts)), typeSupply = nextSupply))

// Lowers a whole program's top-level items one at a time, threading lowering state through them,
// rather than desugaring into one big nested-let expression up front: a top-level
// `let recursive ... and ...` group has no expression-level representation (the language only
// allows `and` groups as top-level declarations, never nested inside another expression), so its
// members must go through lowerPreparedRecursiveGroupWith's own member/continuation split, with
// "the rest of the program" supplied as the continuation lower rather than as a literal Expr — the
// continuation lower ignores the placeholder body it's handed and lowers the remaining items
// instead. Type, external, capability, provider, trait, and implementation declarations are
// registered ahead of lowering by inference and are not part of the value chain, so they are
// skipped here rather than lowered. `environment` is `Some` only from lowerCoreProgramWithEnvironment
// — it enables trait-constrained-value rewriting for plain (non-recursive) top-level lets only;
// recursive bindings and call-site trait-evidence forwarding (rewriteTraitConstrainedReference)
// remain unwired, a deliberately narrower first slice of the trait-dictionary epic.
let recursive lowerCoreProgramItems items trailingBody seen environment state =
    match items with
        | [] -> lowerCore(trailingBody)(state)
        | TopLevelAt(_span, inner) :: rest -> lowerCoreProgramItems(inner :: rest)(trailingBody)(seen)(environment)(state)
        | TopLevelType(declaration) :: rest ->
            match registerTopLevelTypeDeclaration(declaration)(state) with
                | Error(error) -> failure(state)(error)
                | Ok(nextState) -> lowerCoreProgramItems(rest)(trailingBody)(seen)(environment)(nextState)
        | TopLevelLet(LetBindingSyntax { name = name, value = value }, false) :: rest ->
            match checkTopLevelNames([name])(seen) with
                | TopLevelDuplicateCheck { duplicate = Some(duplicateName) } -> failure(state)(DuplicateTopLevelBinding(duplicateName))
                | TopLevelDuplicateCheck { seen = nextSeen, duplicate = None } ->
                    match state with
                        | CoreLoweringState { bindings = outerBindings, constructorLayouts = constructorLayouts } ->
                            let continuation =
                                given (_ignoredBody) ->
                                    given (s) -> lowerCoreProgramItems(rest)(trailingBody)(nextSeen)(environment)(s)
                            in
                                match directSingleArgRcConstructorLayout(value)(constructorLayouts) with
                                    | Some(layout) ->
                                        if topLevelItemsMayReferenceName(rest)(trailingBody)(name)
                                        then lowerArenaBracketedTopLevelLet(name)(value)(environment)(continuation)(outerBindings)(false)(state)
                                        else lowerDeadRcTopLevelLet(name)(value)(layout)(environment)(continuation)(state)
                                    | None ->
                                        lowerArenaBracketedTopLevelLet(name)(value)(environment)(continuation)(outerBindings)(topLevelNameUsedOnlyAsDirectCallee(name)(rest)(trailingBody))(state)
        | TopLevelLet(LetBindingSyntax { name = name, value = value }, true) :: rest ->
            match checkTopLevelNames([name])(seen) with
                | TopLevelDuplicateCheck { duplicate = Some(duplicateName) } -> failure(state)(DuplicateTopLevelBinding(duplicateName))
                | TopLevelDuplicateCheck { seen = nextSeen, duplicate = None } ->
                    match state with
                        | CoreLoweringState { bindings = outerBindings, nextLambdaId = lambdaId } ->
                            []
                            |> prepareRecursiveGroup([(name, value)])(state)
                            |> relabelSingleRecursive(lambdaId)
                            |> lowerPreparedRecursiveGroupWith(
                                [(name, value)],
                                topLevelContinuationBody,
                                lowerCore,
                                given (_ignoredBody) ->
                                    given (s) -> lowerCoreProgramItems(rest)(trailingBody)(nextSeen)(environment)(s),
                                outerBindings
                            )
        | TopLevelRecursiveGroup(bindings) :: rest ->
            match checkTopLevelNames(letBindingSyntaxNames(bindings))(seen) with
                | TopLevelDuplicateCheck { duplicate = Some(duplicateName) } -> failure(state)(DuplicateTopLevelBinding(duplicateName))
                | TopLevelDuplicateCheck { seen = nextSeen, duplicate = None } ->
                    match state with
                        | CoreLoweringState { bindings = outerBindings } ->
                            let pairs = letBindingSyntaxPairs(bindings)
                            in
                                []
                                |> prepareRecursiveGroup(pairs)(state)
                                |> lowerPreparedRecursiveGroupWith(
                                    pairs,
                                    topLevelContinuationBody,
                                    lowerCore,
                                    given (_ignoredBody) ->
                                        given (s) -> lowerCoreProgramItems(rest)(trailingBody)(nextSeen)(environment)(s),
                                    outerBindings
                                )
        | _ :: rest -> lowerCoreProgramItems(rest)(trailingBody)(seen)(environment)(state)

let buildProgram lowered =
    match lowered with
        | LoweredCoreValue { error = Some(error) } -> failedCoreLowering(error)
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            match state with
                | CoreLoweringState { reversedInstructions = instructions, functions = functions, externalFunctions = externalFunctions, externalOpaqueTypes = externalOpaqueTypes, nextLocal = localCount, nextTemp = tempCount, stringLiterals = stringLiterals, pendingOperatorDefaults = pendingOperatorDefaults, sealedOperatorDefaults = sealedOperatorDefaults } ->
                    let resolvedEntryInstructions =
                        instructions
                        |> entryInstructions(temp)
                        |> applyDeferredAdds(state)(pendingOperatorDefaults)
                    in
                        let resolvedFunctions =
                            functions
                            |> applySealedDeferredAdds(state)(sealedOperatorDefaults)
                            |> insertClosureNormalizers(reverse(state.pendingClosureNormalizers))(operatorDefaultedVariables([]
                            |> sealedOperatorTypes(sealedOperatorDefaults)
                            |> append(pendingOperatorTypes(pendingOperatorDefaults)([])))(state))(state)
                        in
                            let entry =
                                IrFunction(
                                    label = "_start_main",
                                    instructions = resolvedEntryInstructions,
                                    localCount = localCount,
                                    tempCount = tempCount,
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
                                                capabilityHandlerGlobals = 0,
                                                traitEvidence = emptyTraitEvidenceAnnotations
                                            )
                                            |> placeLifetimes
                                            |> Some,
                                            semanticType = resolveType(state)(semanticType),
                                            error = None
                                        )

// Seeds the state with the whole-program inspect-only fixpoint over the program's registered
// top-level functions, the verdict `markCallArgumentsMoved` consults for hand-offs.
let withProgramParameterOwnership (program: ProgramSyntax) (state: CoreLoweringState) =
    state with programParameterOwnership = inferProgramParameterOwnership(topLevelFunctions(program))

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
                |> (given (state: CoreLoweringState) -> state with topLevelNames = allTopLevelBindingNames(items))
                |> withProgramParameterOwnership(program)
                |> lowerCoreProgramItems(items)(trailingBody)([])(None)
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
                    state with sourceContext = Some(createSourceContext(filePath)(source)), topLevelNames = allTopLevelBindingNames(items))
                |> withProgramParameterOwnership(program)
                |> lowerCoreProgramItems(items)(trailingBody)([])(None)
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
                |> (given (state: CoreLoweringState) -> state with topLevelNames = allTopLevelBindingNames(items))
                |> withProgramParameterOwnership(program)
                |> lowerCoreProgramItems(items)(trailingBody)([])(Some(environment))
                |> buildProgram

let lowerCoreExpression expression =
    Unit
    |> initialState
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreExpressionWithLayouts layouts expression =
    Unit
    |> initialStateWithLayouts(layouts)
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreExpressionWithContext constructorLayouts builtinLayouts expression =
    Unit
    |> initialStateWithContext(constructorLayouts)(builtinLayouts)
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreExpressionWithFullContext constructorLayouts builtinLayouts externalLayouts externalFunctions externalOpaqueTypes expression =
    Unit
    |> initialStateWithFullContext(constructorLayouts)(builtinLayouts)(externalLayouts)(externalFunctions)(externalOpaqueTypes)
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreExpressionWithCompleteContext constructorLayouts builtinLayouts externalLayouts externalFunctions externalOpaqueTypes capabilityLayouts staticProviders capabilityGlobalCount expression =
    Unit
    |> initialStateWithCompleteContext(constructorLayouts)(builtinLayouts)(externalLayouts)(externalFunctions)(externalOpaqueTypes)(capabilityLayouts)(staticProviders)(capabilityGlobalCount)
    |> lowerCore(expression)
    |> buildProgram

// Lowers an expression that is (part of) the combined item at itemIndex of a stitched project,
// locating every emitted instruction through the context's item regions.
let lowerCoreExpressionLocated (context: SourceContext) (itemIndex: Int) expression =
    Unit
    |> initialState
    |> (given (state: CoreLoweringState) -> state with sourceContext = Some(context), currentItem = itemIndex)
    |> lowerCore(expression)
    |> buildProgram

let lowerCoreRecursiveGroup bindings body =
    Unit
    |> initialState
    |> lowerRecursiveGroup(bindings)(body)(lowerCore)
    |> buildProgram
