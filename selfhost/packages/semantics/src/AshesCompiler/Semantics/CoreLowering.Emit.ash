// The plan records the lowering passes between its steps, the initial state, the fresh temp, local
// and label supply, instruction emission, arena brackets, binding lookup and value placement
// records. A part of `AshesCompiler.Semantics.CoreLowering`.

import AshesCompiler.Semantics.NodeIdentity.withoutNodeIdentity
import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.CoreConstrainedSpecialization
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.StandardTraits.standardTraitEnvironment
import AshesCompiler.Semantics.StandardTraits.withAsyncTaskBuilders
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.ExternalTyping
import AshesCompiler.Semantics.TypeResolution.addTypeDefinition
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.HelperInlining.isInlinableHelperValue
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.MatchArmOwnership
import AshesCompiler.Semantics.ResultReachSummaries.lambdaIdentityOf
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.SourceContext
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
export (
    value standardLoweringTraitEnvironment,
    type FreshTemp(..),
    type FreshLocal(..),
    type FreshType(..),
    type FreshLabel(..),
    type FreshFunctionType(..),
    type LoweredCorePattern(..),
    type CoreIfPlan(..),
    type CoreIfThen(..),
    type MatchScrutineeOwner(..),
    type CoreMatchPlan(..),
    type LoweredMatchArm(..),
    type CoreTagGroup(..),
    type CoreCaseClass(..),
    type PreparedCoreRecursiveBinding(..),
    type PreparedCoreRecursiveGroup(..),
    type PreparedCoreRecursiveMemberResult(..),
    type InstantiatedBinding(..),
    type StringInterning(..),
    type FunctionTypeResolution(..),
    type LoweredCoreValues(..),
    type LoweredCoreBinary(..),
    type CoreBinaryOperator(..),
    type ReservedCoreTemps(..),
    type CoreConstructorShape(..),
    type CoreBuiltinShape(..),
    type CoreCallSpine(..),
    type CoreRecordArguments(..),
    type FreshCoreTypes(..),
    type CorePatternField(..),
    type ArenaBracket(..),
    value emptyScheme,
    value deferredLabelBase,
    value constructorInferenceDefinitionsFromLayouts,
    value extendCoverageEnvironment,
    value extendQueryExternals,
    value seenLayoutTypeName,
    value argumentFreeLayoutTypes,
    value heapLayoutFactsTableFor,
    value initialStateWithCompleteContext,
    value initialStateWithFullContext,
    value initialStateWithContext,
    value initialStateWithLayouts,
    value resultType,
    value standardConstructorLayouts,
    value initialState,
    value withNextTemp,
    value withNextLocal,
    value withTypeSupply,
    value withSubstitution,
    value withNextLambdaId,
    value freshTemp,
    value freshLocal,
    value freshType,
    value freshLabel,
    value freshFunctionType,
    value emit,
    value beginLivePostsGuardBy,
    value beginLivePostsGuard,
    value endLivePostsGuardBy,
    value endLivePostsGuard,
    value emitLivePostsAdjust,
    value emitGuardedRestoreAndReclaimBy,
    value emitGuardedRestoreAndReclaim,
    value currentLocation,
    value withCurrentSpanOf,
    value noteFailureSite,
    value spanStart,
    value sourceFunctionOriginFor,
    value canTestRepresentation,
    value letValueIsLambda,
    value lambdaParameterChain,
    value recordLetLambda,
    value armSourceFunction,
    value sourceFunctionOrigin,
    value lambdaOriginFor,
    value enterFunctionOrigin,
    value openArenaBracket,
    value failure,
    value bindingRequirements,
    value lookupBinding,
    value instantiateBinding,
    value resolveType,
    value resolvedBindingSchemes,
    value isCopyTypeSemantic,
    value recordValuePlacement,
    value recordDecidedRepresentation,
    value armSpecializationLinearParameter,
    value dedupeValuePlacements,
    value finalizeValuePlacements,
    value success,
    value finishClosedLetResult,
    value mismatchSiteOf,
    value bindType,
    value consumerRequestOf,
    value isEmptyConsumerRequest,
    value withConsumerRequest,
    value clearConsumerRequest,
    value expectedTypeOf,
    value withOnlyExpectedType,
    value branchRequest,
    value withLoweredConsumerRequest,
    value runtimeStringRequested,
    value withArgumentRequest,
    value runtimeTempStateOf,
    value isRuntimeTemp,
    value exitResultOwnsItsReference,
    value markRuntimeTemp,
    value markLoweredRuntimeTemp,
    value bodyReturnsRuntimeManaged,
    value recordBodyRuntimeManaged,
    value backfillSelfClosureResultOwnership,
    value recordReturnedClosureLabel,
    value containsLabel,
    value acceptsRuntimeManagedArgument,
    value recordRuntimeNormalizedArgument,
    value releaseConsumedOperandOf,
    value releaseConsumedOperand,
    value unifyExpectedResult,
    value locateLoweredMismatch,
    value argumentSiteOf,
)

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
    | thenArm: MatchArmResult
    | error: Maybe(CoreLoweringError)

// The owner an arm makes for a fresh runtime-managed scrutinee, stage 0's `$match_rc_N`: the
// scrutinee's owned type name, and whether it is a list whose spine the release walks.
type MatchScrutineeOwner =
    | scrutineeTypeName: Str
    | scrutineeIsList: Bool
    | scrutineeType: SemanticType

// The plan of one match: the arms lowered so far record what each stored into the result slot
// (most recent first), the scrutinee owner every arm adopts when the scrutinee is a fresh
// runtime-managed value, and whether the literal string arms are normalized to the
// reference-counted heap.
type CoreMatchPlan =
    | state: CoreLoweringState
    | valueTemp: Int
    | valueType: SemanticType
    | resultSlot: Int
    | endLabel: Str
    | noMatchLabel: Str
    | resultType: SemanticType
    | armRequest: ConsumerRequest
    | armResults: List(MatchArmResult)
    | scrutineeOwner: Maybe(MatchScrutineeOwner)
    | normalizeStaticStrings: Bool
    // The reuse-eligible scrutinee's name with whether its tokens are reference-counted cells
    // (a `let`-owned value) or arena cells (a linear loop accumulator).
    | reuseScrutineeName: Maybe((Str, Bool))
    // A tuple scrutinee's elements, each the loop parameter slot it reads when it is one.
    | scrutineeParameterSlots: List(Maybe(Int))
    | error: Maybe(CoreLoweringError)

// One lowered arm: its lowered value, and what it contributed to the match's result.
type LoweredMatchArm =
    | lowered: LoweredCoreValue
    | armResult: MatchArmResult

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

// The first label id of the range the deferred reset blocks are emitted with, past any id a
// program allocates on its own.
let deferredLabelBase = 1000000

// The standard trait environment every lowering starts from, built once for the whole program.
let standardLoweringTraitEnvironment = standardTraitEnvironment(Unit)

let recursive constrainedBuiltinBindings layouts =
    match layouts with
        | [] -> []
        | CoreBuiltinLayout { moduleName = moduleName, memberName = memberName, scheme = scheme } :: rest -> (moduleName + "." + memberName, scheme) :: constrainedBuiltinBindings(rest)

let recursive standardTraitQueryAliases bindings =
    match bindings with
        | [] -> []
        | (name, scheme) :: rest -> ("Ashes.Trait." + name, scheme) :: standardTraitQueryAliases(rest)

let standardTraitQueryBindings =
    match standardLoweringTraitEnvironment with
        | TypeEnvironment { bindings = bindings } ->
            bindings
            |> standardTraitQueryAliases
            |> append(bindings)

// The coverage and reachability rules live in `TypeInference.ash`'s `matchCoverageError` — the
// exact checker the project-inference path runs — fed with a minimal `TypeEnvironment` carrying
// the live constructor layouts (intrinsic and user-declared alike, deep-copied out of the
// long-lived state), so the single-file lowering path reports the same non-exhaustive-match,
// unreachable-arm, and mixed-ADT diagnostics with stage 0's wording. The heap layout classifiers
// read the same environment. It is rebuilt only where the layouts change and kept on the state:
// copying every constructor of the program at each query made lowering quadratic in them.
let recursive constructorInferenceDefinitionsFromLayouts layouts =
    match layouts with
        | [] -> []
        | CoreConstructorLayout { name = name, scheme = scheme, fieldNames = fieldNames } :: rest ->
            ConstructorInferenceDefinition(
                name = Ashes.Internal.deepCopy(name),
                scheme = Ashes.Internal.deepCopy(scheme),
                fieldNames = Ashes.Internal.deepCopy(fieldNames)
            ) :: constructorInferenceDefinitionsFromLayouts(rest)

let recursive schemeResultType (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(_parameter, result, _effects) -> schemeResultType(result)
        | other -> other

let recursive constructorQueryBindings definitions =
    match definitions with
        | [] -> []
        | ConstructorInferenceDefinition { name = name, scheme = scheme } :: rest -> (name, scheme) :: constructorQueryBindings(rest)

let recursive constructorQueryContext definitions context =
    match definitions with
        | [] -> context
        | ConstructorInferenceDefinition { scheme = TypeScheme { body = body } } :: rest ->
            match schemeResultType(body) with
                | SemNamed(symbol, name, arguments) ->
                    context
                    |> addTypeDefinition(symbol)(name)(length(arguments))
                    |> constructorQueryContext(rest)
                | _ -> constructorQueryContext(rest)(context)

// Constructor values and named type definitions are cached alongside coverage
// metadata when layouts change, not reconstructed for each call inference query.
let extendCoverageEnvironment (environment: TypeEnvironment) layouts =
    (let definitions = constructorInferenceDefinitionsFromLayouts(layouts)
    in
        let extended = heapExtendConstructorEnvironment(environment)(definitions)
        in
            extended with bindings = append(environment.bindings)(constructorQueryBindings(definitions)), typeResolutionContext = constructorQueryContext(definitions)(environment.typeResolutionContext))

let recursive extendQueryExternals functions environment =
    match functions with
        | [] -> environment
        | ExternalFunctionAbi { name = name, sourceTyping = ExternalFunctionTyping { directType = directType, firstClassType = firstClassType } } :: rest ->
            let firstClass =
                match firstClassType with
                    | None -> None
                    | Some(semanticType) ->
                        semanticType
                        |> emptyScheme
                        |> Some
            in
                environment
                |> addExternalFunctionBinding(name)(emptyScheme(directType))(firstClass)
                |> extendQueryExternals(rest)

let recursive seenLayoutTypeName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || seenLayoutTypeName(name)(rest)

// The declared named types that take no type arguments, once each: their layout does not depend on
// an instantiation, so they are the ones worth classifying ahead of the questions.
let recursive argumentFreeLayoutTypes (layouts: List(CoreConstructorLayout)) (seen: List(Str)) =
    match layouts with
        | [] -> []
        | CoreConstructorLayout { scheme = TypeScheme { body = body } } :: rest ->
            match schemeResultType(body) with
                | SemNamed(_symbolId, name, []) as named ->
                    if seenLayoutTypeName(name)(seen)
                    then argumentFreeLayoutTypes(rest)(seen)
                    else (name, named) :: argumentFreeLayoutTypes(rest)(name :: seen)
                | _ -> argumentFreeLayoutTypes(rest)(seen)

// The classifier walks a type's whole graph once per question it answers, and lowering asks it at
// every call site, so the declared argument-free types are classified once whenever the layouts
// change. A classification that still holds an unresolved type is left out: inference may yet close
// it, and the live answer must be read then.
let recursive heapLayoutFactsTableOf (types: List((Str, SemanticType))) (environment: TypeEnvironment) (table: MapTree(Str, HeapLayoutFacts)) =
    match types with
        | [] -> table
        | (name, named) :: rest ->
            match classifyHeapLayout(named)(environment) with
                | HeapLayoutFacts { containsUnresolvedType = true } -> heapLayoutFactsTableOf(rest)(environment)(table)
                | facts ->
                    table
                    |> Ashes.Collection.Map.setStr(name)(facts)
                    |> heapLayoutFactsTableOf(rest)(environment)

let heapLayoutFactsTableFor (layouts: List(CoreConstructorLayout)) (environment: TypeEnvironment) =
    heapLayoutFactsTableOf(argumentFreeLayoutTypes(layouts)([]))(environment)(Ashes.Collection.Map.empty)

let coverageEnvironmentOf layouts builtinLayouts =
    match standardLoweringTraitEnvironment with
        | TypeEnvironment { typeResolutionContext = context } ->
            let empty = emptyTypeEnvironment(Unit)
            in
                (empty with bindings = append(standardTraitQueryBindings)(constrainedBuiltinBindings(builtinLayouts)), typeResolutionContext = addTypeDefinition(0)("Task")(2)(context))
                |> withAsyncTaskBuilders(0)
                |> (given (environment) -> extendCoverageEnvironment(environment)(layouts))

let initialStateWithCompleteContext constructorLayouts builtinLayouts externalLayouts externalFunctions externalOpaqueTypes capabilityLayouts staticProviders capabilityGlobalCount unit =
    (let coverageEnvironment =
        builtinLayouts
        |> coverageEnvironmentOf(constructorLayouts)
        |> extendQueryExternals(externalFunctions)
    in
        CoreLoweringState(
            reversedInstructions = [],
            functions = [],
            bindings = [],
            externalFunctions = externalFunctions,
            externalOpaqueTypes = externalOpaqueTypes,
            nextTemp = 0,
            nextLocal = 0,
            nextLambdaId = 0,
            nextLabelId = 0,
            stringLiterals = [],
            // Starts past `standardBuiltinLayouts`' own reserved ids (see
            // `reservedBuiltinTypeVariableCount`'s own comment) rather than `0` — even a caller
            // passing empty `constructorLayouts`/`builtinLayouts` gets this, harmlessly: it just means
            // this compilation's fresh-variable numbering never mints those specific low ids, not that
            // anything is missing.
            typeSupply = typeVariableSupplyFrom(reservedBuiltinTypeVariableCount),
            substitution = Ashes.Collection.Map.empty,
            currentSpan = None,
            currentNode = None,
            consumerRequest = emptyConsumerRequest,
            runtimeTemps = Ashes.Collection.Map.empty,
            reuseTokens = [],
            valuePlacements = [],
            joinRepresentations = [],
            borrowedCalls = [],
            programState = CoreProgramState(
                constructorLayouts = constructorLayouts,
                constructorIndex = constructorLookupIndex(constructorLayouts),
                coverageTypes = coverageEnvironment,
                coverageEnvironmentVersion = 0,
                coverageOpenSchemes = None,
                coverageOperatorConstrainedNames = None,
                heapQueryContext = emptyCoreHeapQueryContext(coverageEnvironment),
                heapLayoutFactsByType = coverageEnvironment |> heapLayoutFactsTableFor(constructorLayouts),
                dropperTypes = constructorLayouts
                |> constructorInferenceDefinitionsFromLayouts
                |> prepareDropperTypes,
                builtinLayouts = builtinLayouts,
                externalLayouts = externalLayouts,
                declaredTypeNames = Ashes.Collection.Map.empty,
                typeAliases = Ashes.Collection.Map.empty,
                capabilityLayouts = capabilityLayouts,
                staticProviders = staticProviders,
                capabilityGlobalCount = capabilityGlobalCount,
                capabilityOperationSchemes = [],
                handledCapabilities = [],
                failureSite = None,
                sourceContext = None,
                sourceRegionOfName = Ashes.Collection.Map.empty,
                topLevelNames = Ashes.Collection.Map.empty,
                letLambdas = [],
                letLambdasByName = Ashes.Collection.Map.empty,
                moveFunctionTable = [],
                moveCallSites = [],
                moveNullaryConstructors = [],
                liveHandlerFunctions = Ashes.Collection.Map.empty,
                parallelWorkerFunctions = Ashes.Collection.Map.empty,
                entryRunsUnderLiveHandlerPost = false,
                anyRunsUnderLiveHandlerPost = false,
                patternOwnerSites = [],
                cellMarkerSlots = [],
                tupleBinderParameters = [],
                bodyRuntimeManagedByLabel = Ashes.Collection.Map.empty,
                generalRcOwnedResultLabels = [],
                discardedResultSpans = [],
                tupleResultPartLabels = Ashes.Collection.Map.empty,
                normalizableRecordTypes = Ashes.Collection.Map.empty,
                letLambdaLabels = Ashes.Collection.Map.empty,
                letLambdaIdentities = Ashes.Collection.Map.empty,
                reachSummaries = [],
                reachRegistry = None,
                reachTable = Ashes.Collection.Map.empty,
                reachByIdentity = Ashes.Collection.Map.empty,
                reachByName = Ashes.Collection.Map.empty,
                callReachByKey = Ashes.Collection.Map.empty,
                programParameterOwnership = Ashes.Collection.Map.empty,
                dropperLabels = emptyDropperLabelCache,
                resultRcEligibility = None,
                generalRcTypeFacts = Ashes.Collection.Map.empty,
                contractRecordFacts = Ashes.Collection.Map.empty,
                inlinableHelpers = [],
                specializationCandidates = [],
                specializationCandidateByName = Ashes.Collection.Map.empty,
                topLevelFunctionRefs = [],
                topLevelInferenceBindings = [],
                topLevelInferenceOpen = [],
                topLevelInferenceOpenShadowed = false,
                topLevelInferenceEnvironment = None,
                topLevelFunctionRefByName = Ashes.Collection.Map.empty,
                traitEnvironment = standardLoweringTraitEnvironment,
                traitMethodLabels = Ashes.Collection.Map.empty,
                closureDropperLabels = [],
                provenTraitGoals = Ashes.Collection.Map.empty,
                provenSiblingCallResults = Ashes.Collection.Map.empty,
                typeObservation = None,
                nodeTypes = None,
                nodeInstantiation = [],
                settledNodeVariables = Ashes.Collection.Map.empty
            ),
            functionState = CoreFunctionState(
                admissibilityAnswers = [],
                deferredLabelNext = deferredLabelBase,
                deferredLabelGroups = [],
                nextStringId = 0,
                currentItem = 0,
                pendingStackClosure = false,
                coroutineBody = false,
                pendingCoroutineBody = false,
                helperFactoryType = None,
                completedTaskFrameDropper = None,
                runtimeAdtRequested = false,
                pendingOperatorDefaults = [],
                enclosingBindings = [],
                letBoundFunctionLabels = [],
                inlineTypeVariables = [],
                sealedOperatorDefaults = [],
                pendingSourceFunction = None,
                activeFunctionOrigin = None,
                underLiveHandlerPost = false,
                mayRunAsParallelWorker = false,
                handlerArmOwnResult = false,
                pendingClosureNormalizers = [],
                resourceStates = [],
                recursiveDeclarationSpan = None,
                recursiveMemberStarts = [],
                activeTraitMethods = [],
                activeTypeParameters = [],
                pendingTraitGoals = [],
                pendingEqualityGoals = [],
                pendingSiblingCallResults = [],
                traitGoalsPinned = false
            ),
            ownerState = CoreOwnerState(
                retiredLocals = [],
                backEdgeDummyTemps = [],
                reuseResultTemps = [],
                patternOwnerCopyTemps = [],
                releasedPatternOwnerDuplicates = [],
                runtimeOwners = [],
                borrowedOwners = [],
                pendingBorrowedOwners = [],
                patternOwnerResultTemps = [],
                ownerReleasePlans = [],
                generalRcOwnedSlots = [],
                generalRcPredecessorSlots = [],
                generalRcSlotBinders = [],
                generalRcPredecessorOwners = [],
                generalRcFreshSlots = [],
                generalRcHandedOverPartsSlots = [],
                generalRcPartDuplicateSlots = [],
                generalRcAliasOwnerSlots = [],
                loopResultPartDuplicates = [],
                takenOverConsumers = [],
                takenOverRecordNames = [],
                escapingCaptureNames = [],
                takenOverSlotSources = [],
                spineFreedSpans = [],
                ownFieldRetainedParameters = [],
                ownFieldPendingParameters = [],
                generalRcBranches = [],
                generalRcSlotBranches = [],
                generalRcFinishedResult = None,
                closedProducerChain = None,
                pendingOwnerPlan = None,
                unresolvedCallResults = [],
                resolvingCallsAgain = false,
                runtimeOwnerAliases = [],
                pendingRuntimeArgumentFlags = [],
                genericDeepCopiedListTemps = [],
                ownedReferenceJoins = [],
                retainedArmJoins = []
            ),
            tcoState = CoreTcoState(
                tcoParameterRetainSites = [],
                arenaParameterCopies = [],
                borrowedScrutineeCells = [],
                transientFlagClears = [],
                backEdgeArgumentSlot = None,
                lateCopiedCellHeads = [],
                arenaSuccessorLetLoop = None,
                arenaKeptSuccessorLetLoop = None,
                arenaSuccessorJoin = None,
                affineAppendContext = None,
                affineAppendReservation = None,
                recursiveProducerResultSlots = [],
                normalizedAlwaysReturnedParameter = None,
                normalizedParameterCloned = false,
                runtimeNormalizedArgumentLabels = [],
                recursiveGroupNames = [],
                tcoLoop = None,
                functionReturnedClosureLabels = Ashes.Collection.Map.empty,
                passthroughParameter = None,
                loweringBodyLabel = "",
                predictedRuntimeManagedResultLabels = [],
                curryStage = None,
                tcoLoopFrame = None,
                tmcActivatedLabels = [],
                pendingTcoResets = [],
                pendingCallCopyOuts = [],
                pendingTakenOverSettles = [],
                takenOverHeldResults = [],
                takenOverCallArguments = [],
                settledBehindCopySpans = [],
                withdrawnTuplePartsRequests = [],
                listHeadCells = [],
                selfResultDeferredNames = [],
                siblingResultOpenNames = [],
                selfCallResultUnifications = [],
                callArgumentRecordRetains = [],
                collectingCallArgumentRetains = false,
                callArgumentLabelStart = 0,
                callArgumentFrameActive = false
            ),
            reuseState = CoreReuseState(
                reuseTransferredNames = [],
                reuseEnabled = true,
                linearReuseNames = [],
                directReuseCandidates = [],
                inliningInProgress = [],
                reuseSpecializations = [],
                specializingLinearParam = None,
                specializingInProgress = [],
                specializationFreshInputs = None,
                stackAllocateConstructor = false,
                scrutineeTuple = false,
                linearSpecializationAccumulators = [],
                routedSpecializationAccumulators = [],
                specializingReuseLabel = None,
                fullyReusingCallees = [],
                concreteRegistry = emptyCoreConcreteRegistry(Unit),
                concreteDepth = 0,
                concreteDeclines = 0
            )
        ))

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

// `Ordering` is part of the seeded standard-trait ABI, not something a program opts into: `Ord`'s
// `compare` returns it, and the comparison operators read its tag whether or not the source ever
// names it. Its tags follow the declaration order in `lib/Ashes/Trait.ash`, which is also the order
// the shipped type declaration produces when a program does stitch it.
let orderingLayoutType = SemNamed(0)("Ordering")([])

let orderingLayoutScheme = TypeScheme(quantified = [], body = orderingLayoutType, constraints = [])

let orderingConstructorLayout name tag =
    CoreConstructorLayout(
        name = name,
        tag = tag,
        scheme = orderingLayoutScheme,
        fieldNames = [],
        isZeroCost = false,
        tagless = false
    )

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
        ),
        orderingConstructorLayout("Less")(0),
        orderingConstructorLayout("Equal")(1),
        orderingConstructorLayout("Greater")(2),
        orderingConstructorLayout("Unordered")(3)
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

let withSubstitution substitution (state: CoreLoweringState) = clearAdmissibilityAnswers((state with substitution = substitution))

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
        | CoreLoweringState { reversedInstructions = instructions, currentSpan = span, programState = CoreProgramState { sourceContext = context }, functionState = CoreFunctionState { currentItem = item } } ->
            let wrapped = tagItemInstruction(kind)(span)(item)(context)
            in state with reversedInstructions = wrapped :: instructions

// The index of the live-posts counter among the capability globals: one past the pending-post
// register that follows the capability handler slots.
let livePostsIndex (state: CoreLoweringState) = stateCapabilityGlobalCount(state) + 1

// Stage 0's `BeginLivePostsGuard`: with a capability in the program, an arena restore is skipped
// while a one-shot post is pending, since the post's closure still lives in the window. A post
// an arm has stored in the pending-post register but its perform site has not yet counted is
// pending too: a `let` scoped over the arm's one-shot `resume` closes before the arm returns. The
// guard reads the live-posts counter and the pending-post register and jumps past the block when
// either is not zero; without a capability nothing is emitted and there is no label to close.
// `place` adds each instruction: `emit` for a block lowered in place, `unlocatedInstruction` for
// a block spliced in after the body is lowered.
let beginLivePostsGuardBy place (state: CoreLoweringState) =
    if stateCapabilityGlobalCount(state) == 0
    then (state, None)
    else
        match freshTemp(state) with
            | FreshTemp { state = counterState, temp = counterTemp } ->
                match freshTemp(counterState) with
                    | FreshTemp { state = pendingState, temp = pendingTemp } ->
                        match freshTemp(pendingState) with
                            | FreshTemp { state = guardedState, temp = guardedTemp } ->
                                match freshTemp(guardedState) with
                                    | FreshTemp { state = zeroState, temp = zeroTemp } ->
                                        match freshTemp(zeroState) with
                                            | FreshTemp { state = isZeroState, temp = isZeroTemp } ->
                                                let skipLabel = "live_posts_skip_" + Ashes.Text.fromInt(counterTemp)
                                                in
                                                    isZeroState
                                                    |> place(state
                                                    |> livePostsIndex
                                                    |> LoadCapabilityHandler(counterTemp))
                                                    |> place(state
                                                    |> stateCapabilityGlobalCount
                                                    |> LoadCapabilityHandler(pendingTemp))
                                                    |> place(OrInt(guardedTemp)(counterTemp)(pendingTemp))
                                                    |> place(LoadConstInt(zeroTemp)(0))
                                                    |> place(CmpIntEq(isZeroTemp)(guardedTemp)(zeroTemp))
                                                    |> place(JumpIfFalse(isZeroTemp)(skipLabel))
                                                    |> (given (guarded) -> (guarded, Some(skipLabel)))

let beginLivePostsGuard (state: CoreLoweringState) = beginLivePostsGuardBy(emit)(state)

let endLivePostsGuardBy place (skipLabel: Maybe(Str)) (state: CoreLoweringState) =
    match skipLabel with
        | Some(label) -> place(Label(label))(state)
        | None -> state

let endLivePostsGuard (skipLabel: Maybe(Str)) (state: CoreLoweringState) = endLivePostsGuardBy(emit)(skipLabel)(state)

// Stage 0's `EmitLivePostsAdjust`: adds `delta` to the live-posts counter, which a perform site
// raises when it pushes a one-shot post and the posts fold lowers when it has run one, so every
// live-posts guard sees the post as pending until its fold.
let emitLivePostsAdjust (delta: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = counterState, temp = counterTemp } ->
            match freshTemp(counterState) with
                | FreshTemp { state = deltaState, temp = deltaTemp } ->
                    match freshTemp(deltaState) with
                        | FreshTemp { state = adjustedState, temp = adjustedTemp } ->
                            adjustedState
                            |> emit(state
                            |> livePostsIndex
                            |> LoadCapabilityHandler(counterTemp))
                            |> emit(LoadConstInt(deltaTemp)(delta))
                            |> emit(AddInt(adjustedTemp)(counterTemp)(deltaTemp))
                            |> emit(StoreCapabilityHandler(livePostsIndex(state))(adjustedTemp))

// An arena restore and its reclaim under the live-posts guard, the shape of every scope, call and
// back-edge reset stage 0 skips while a one-shot post is pending.
let emitGuardedRestoreAndReclaimBy place cursorSlot endSlot preRestoreSlot (state: CoreLoweringState) =
    match beginLivePostsGuardBy(place)(state) with
        | (guarded, skipLabel) ->
            guarded
            |> place(RestoreArenaState(cursorSlot)(endSlot)(preRestoreSlot)(false))
            |> place(ReclaimArenaChunks(endSlot)(preRestoreSlot)(false))
            |> endLivePostsGuardBy(place)(skipLabel)

let emitGuardedRestoreAndReclaim cursorSlot endSlot preRestoreSlot (state: CoreLoweringState) = emitGuardedRestoreAndReclaimBy(emit)(cursorSlot)(endSlot)(preRestoreSlot)(state)

// The innermost enclosing span resolved the way emitted instructions resolve theirs.
let currentLocation (state: CoreLoweringState) =
    match (state.currentSpan, stateSourceContext(state)) with
        | (Some(span), Some(context)) ->
            resolveItemSpanLocation(context)(stateCurrentItem(state))(span)
        | _ -> None

let recordFailureSite (span: TextSpan) (failed: CoreLoweringError) (state: CoreLoweringState) =
    match currentLocation((state with currentSpan = Some(span))) with
        | Some(location) -> withStateFailureSite(Some((failed, location)))(state)
        | None -> state

let withCurrentSpanOf (span: Maybe(TextSpan)) (state: CoreLoweringState) = state with currentSpan = span

// Records an error leaving the expression spanned `span` at that expression's location, unless
// the same error was already recorded, by an inner expression, so the innermost location stays.
let noteFailureSite (span: TextSpan) (error: Maybe(CoreLoweringError)) (state: CoreLoweringState) =
    match error with
        | None -> state
        | Some(failed) ->
            match stateFailureSite(state) with
                | Some((recorded, _location)) ->
                    if recorded == failed
                    then state
                    else recordFailureSite(span)(failed)(state)
                | None -> recordFailureSite(span)(failed)(state)

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

// Stage 0's `CanTestRepresentation`. A consumer that must own a value it cannot see the
// representation of asks the value at run time — `IsReferenceCounted` — and takes a reference
// instead of a copy when the answer is yes. Stage 0 withholds that from a function that may run as
// a structured-parallelism worker, whose arena another thread tears down independently of this
// function's scope.
let canTestRepresentation (state: CoreLoweringState) = stateMayRunAsParallelWorker(state) == false

let recursive letValueIsLambda (value: Expr) =
    match value with
        | ExprAt(_span, inner) -> letValueIsLambda(inner)
        | ExprNode(_node, inner) -> letValueIsLambda(inner)
        | ExprLambda(_parameter, _body, _annotation) -> true
        | _ -> false

// Arms a `let` whose value is a lambda so the lambda lowered next is lifted as that name's
// `SourceFunction`; any other value leaves the lambda origins alone. A lambda under `let ... in`
// wrappers inside the value is not armed and lifts as a closure helper.
// The parameter chain and innermost body of a curried lambda value.
// The innermost body keeps its own source-location wrapper, so a body spliced into a call site
// (an inlined helper) is located where it was written rather than at the call.
let recursive lambdaParameterChain (value: Expr) (parameters: List(Str)) =
    match value with
        | ExprAt(_span, inner) ->
            match inner with
                | ExprAt(_innerSpan, _nested) -> lambdaParameterChain(inner)(parameters)
                | ExprNode(_node, _nested) ->
                    match withoutNodeIdentity(inner) with
                        | ExprLambda(_parameter, _body, _annotation) -> lambdaParameterChain(inner)(parameters)
                        | _ -> (reverse(parameters), value)
                | ExprLambda(_parameter, _body, _annotation) -> lambdaParameterChain(inner)(parameters)
                | _ -> (reverse(parameters), value)
        | ExprNode(_node, inner) ->
            match withoutNodeIdentity(inner) with
                | ExprLambda(_parameter, _body, _annotation) -> lambdaParameterChain(inner)(parameters)
                | ExprAt(_innerSpan, _nested) -> lambdaParameterChain(inner)(parameters)
                | _ -> (reverse(parameters), value)
        | ExprLambda(parameter, body, _annotation) -> lambdaParameterChain(body)(parameter :: parameters)
        | body -> (reverse(parameters), body)

// A let-bound lambda is remembered by name so a later call through it can ask which of its
// parameters it only borrows (stage 0's per-function ownership summary).
let recordLetLambda (name: Str) (value: Expr) (state: CoreLoweringState) =
    match lambdaParameterChain(value)([]) with
        | (parameters, body) ->
            state
            |> withStateLetLambdas((name, parameters, body) :: stateLetLambdas(state))
            |> withStateLetLambdasByName(state
            |> stateLetLambdasByName
            |> Ashes.Collection.Map.setStr(name)((parameters, body)))
            |> withStateLetLambdaIdentities(state
            |> stateLetLambdaIdentities
            |> Ashes.Collection.Map.setStr(name)(lambdaIdentityOf(value)))

// A non-recursive let-bound lambda whose body allocates or calls is registered as an inlinable
// helper, stage 0's `RegisterInlinableNonRecursiveLet`.
let registerInlinableHelper (name: Str) (value: Expr) (state: CoreLoweringState) =
    if isInlinableHelperValue(value)
    then withStateInlinableHelpers(name :: stateInlinableHelpers(state))(state)
    else state

let armSourceFunction (name: Str) (value: Expr) (stackClosure: Bool) (state: CoreLoweringState) =
    if letValueIsLambda(value)
    then
        state
        |> withStatePendingSourceFunction(state
        |> sourceFunctionOriginFor(name)
        |> Some)
        |> withStatePendingStackClosure(stackClosure)
        |> recordLetLambda(name)(value)
        |> registerInlinableHelper(name)(value)
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
    match statePendingSourceFunction(state) with
        | Some(source) -> sourceFunctionOrigin(label)(source)(state)
        | None ->
            closureHelperOrigin(label)(parameter)(stateActiveFunctionOrigin(state))(state)

// A top-level function takes its own live-handler answer; any other function keeps the answer
// of the function it is lowered inside, stage 0's fallback to the enclosing context.
let functionUnderLiveHandlerPost (origin: IrFunctionOrigin) (state: CoreLoweringState) =
    match origin.sourceOrigin with
        | Some(SourceFunctionOrigin { functionSourceName = name }) ->
            match state
            |> stateTopLevelNames
            |> Ashes.Collection.Map.getStr(name) with
                | Some(_) ->
                    match state
                    |> stateLiveHandlerFunctions
                    |> Ashes.Collection.Map.getStr(name) with
                        | Some(_) -> true
                        | None -> false
                | None -> stateUnderLiveHandlerPost(state)
        | None -> stateUnderLiveHandlerPost(state)

// A top-level function takes its own parallel-worker answer; any other function keeps the answer
// of the function it is lowered inside.
let functionMayRunAsParallelWorker (origin: IrFunctionOrigin) (state: CoreLoweringState) =
    match origin.sourceOrigin with
        | Some(SourceFunctionOrigin { functionSourceName = name }) ->
            match state
            |> stateTopLevelNames
            |> Ashes.Collection.Map.getStr(name) with
                | Some(_) ->
                    match state
                    |> stateParallelWorkerFunctions
                    |> Ashes.Collection.Map.getStr(name) with
                        | Some(_) -> true
                        | None -> false
                | None -> stateMayRunAsParallelWorker(state)
        | None -> stateMayRunAsParallelWorker(state)

let enterFunctionOrigin (origin: IrFunctionOrigin) (state: CoreLoweringState) =
    state
    |> withStateUnderLiveHandlerPost(functionUnderLiveHandlerPost(origin)(state))
    |> withStateMayRunAsParallelWorker(functionMayRunAsParallelWorker(origin)(state))
    |> withStatePendingSourceFunction(None)
    |> withStatePendingStackClosure(false)
    |> withStateActiveFunctionOrigin(Some(origin))

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

let bindingRequirements (binding: CoreBinding) =
    (let scheme = binding.scheme
    in scheme.constraints)

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
        | CoreLoweringState { substitution = substitution } -> applySubstitutionMap(substitution)(semanticType)

// The environment a `let` generalizes against, read through the current substitution: a variable
// an enclosing binding's type was unified with since its scheme was recorded (a captured
// parameter whose variable now stands for a constructor field's) is the environment's and must
// not be quantified, the Hindley-Milner rule stage 0 follows by pruning the environment first.
// A closed scheme has no variable to hold back, so the environment lists the open ones only.
let recursive resolvedBindingSchemes (bindings: List(CoreBinding)) (state: CoreLoweringState) =
    match bindings with
        | [] -> []
        | CoreBinding { scheme = scheme, closedScheme = closed } :: rest ->
            if closed
            then resolvedBindingSchemes(rest)(state)
            else applySchemeSubstitutionMap(state.substitution)(scheme) :: resolvedBindingSchemes(rest)(state)

// A copy-typed value needs no heap ownership at all, so it always reports as `CopyValue` in the
// explain report's `memory` representation, regardless of how it was produced.
let isCopyTypeSemantic (semanticType: SemanticType) =
    match semanticType with
        | SemInt -> true
        | SemUInt(_bits) -> true
        | SemFloat -> true
        | SemRune -> true
        | SemBool -> true
        | _ -> false

// The per-value fact the explain report's `memory` representation needs and nowhere else does:
// which function produced this temp, and its type at the point it was produced. Recording it
// here — where every lowered value already passes through with its type — reads state and appends
// to a list; it changes nothing about what gets lowered. The type is kept unresolved: a builtin
// call's argument is lowered (and its `success` recorded) before the call site unifies the
// argument's type with the parameter's, so resolving eagerly here would still see a bare type
// variable for an Int parameter — `finalizeValuePlacements` resolves every entry once, against
// the whole program's finished substitution, right before the lowering result is built.
// Stage 0 only captures a value placement from inside `AddFunction`, called for a lifted function
// and never for the program entry, so a value lowered while `activeFunctionOrigin` is still `None`
// (the top-level trailing expression, before any lambda is entered) is not recorded either.
// A negative temp is a sentinel used by recursive-walk base cases (e.g. `success(-1)(SemNever)(state)`
// for an empty list) that never reaches a real IR value — skip it rather than recording a phantom placement.
let recordValuePlacement (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match (temp < 0, stateActiveFunctionOrigin(state)) with
        | (true, _) -> state
        | (false, None) -> state
        | (false, Some(_origin)) -> state with valuePlacements = (temp, stateActiveFunctionOrigin(state), semanticType) :: state.valuePlacements

// The representation of a value the lowering knows and the `memory` report's post-hoc walk over
// the emitted instructions cannot recover, because the value is reloaded from a slot more than one
// branch wrote. `(false, false)` is the walk's own conservative-unknown, recorded here when the
// answer genuinely depends on which branch ran.
let recordDecidedRepresentation (temp: Int) (isArena: Bool) (isRc: Bool) (state: CoreLoweringState) = state with joinRepresentations = (stateActiveFunctionOrigin(state), temp, isArena, isRc) :: state.joinRepresentations

// Stage 0's `LowerLambdaCoreSeedScopeBindings` reuse arm: the parameter a specialization is being
// generated for becomes a linear reuse root, so a match on it hands its dead cells to the arms as
// arena reuse tokens. The request is consumed here, so a nested lambda never inherits it.
let armSpecializationLinearParameter (parameter: Str) (origin: IrFunctionOrigin) (state: CoreLoweringState) =
    match (stateSpecializingLinearParam(state), origin) with
        | (Some(linear), IrFunctionOrigin { generatedLabel = label }) ->
            if linear == parameter
            then
                state
                |> withStateLinearReuseNames([parameter])
                |> withStateSpecializingLinearParam(None)
                |> withStateSpecializingReuseLabel(Some(label))
            else state
        | _ -> state

// A placement's identity: a function's generated label is unique, so the label and temp name the
// (temp, origin) pair without comparing the origin records.
let valuePlacementKey (temp: Int) (origin: Maybe(IrFunctionOrigin)) =
    match origin with
        | Some(IrFunctionOrigin { generatedLabel = label }) -> label + "#" + Ashes.Text.fromInt(temp)
        | None -> "#" + Ashes.Text.fromInt(temp)

// A temp is produced exactly once (`freshTemp`'s counter never repeats within one function), so
// the same (temp, origin) pair recorded more than once always names the same value passing
// through more than one `success`-shaped wrapper on its way out of lowering — a let's closing
// bracket returning its body's own result temp unchanged, an aggregate's child retain that turned
// out to be a no-op — rather than two different values sharing a number. Keeping the first
// occurrence and dropping the rest turns each such re-wrap back into the single placement stage 0
// would have recorded. The kept placements accumulate in reverse and are reversed once at the end,
// so a program's whole placement list costs no stack.
let recursive dedupeValuePlacementsInto (seen: MapTree(Str, Bool)) (placements: List((Int, Maybe(IrFunctionOrigin), SemanticType))) (kept: List((Int, Maybe(IrFunctionOrigin), SemanticType))) =
    match placements with
        | [] -> reverse(kept)
        | (temp, origin, semanticType) :: rest ->
            let key = valuePlacementKey(temp)(origin)
            in
                match Ashes.Collection.Map.getStr(key)(seen) with
                    | Some(_present) -> dedupeValuePlacementsInto(seen)(rest)(kept)
                    | None ->
                        dedupeValuePlacementsInto(Ashes.Collection.Map.setStr(key)(true)(seen))(rest)((temp, origin, semanticType) :: kept)

let dedupeValuePlacements (placements: List((Int, Maybe(IrFunctionOrigin), SemanticType))) = dedupeValuePlacementsInto(Ashes.Collection.Map.empty)(placements)([])

// Resolves every recorded value placement's type against the finished program's substitution and
// classifies it, turning the deduplicated `(temp, origin, semanticType)` list `CoreLoweringState`
// grows during lowering into the `(temp, origin, isCopyType)` list `CoreLoweringResult` exports.
let recursive finalizeValuePlacementsInto (state: CoreLoweringState) (placements: List((Int, Maybe(IrFunctionOrigin), SemanticType))) (finalized: List((Int, Maybe(IrFunctionOrigin), Bool))) =
    match placements with
        | [] -> reverse(finalized)
        | (temp, origin, semanticType) :: rest ->
            finalizeValuePlacementsInto(state)(rest)((temp, origin, semanticType
            |> resolveType(state)
            |> isCopyTypeSemantic) :: finalized)

let finalizeValuePlacements (state: CoreLoweringState) (placements: List((Int, Maybe(IrFunctionOrigin), SemanticType))) = finalizeValuePlacementsInto(state)(placements)([])

let success temp semanticType state =
    LoweredCoreValue(
        state = recordValuePlacement(temp)(semanticType)(state),
        temp = temp,
        semanticType = semanticType,
        error = None
    )

// Wraps a `let`'s closed-bracket result as a `LoweredCoreValue`, without recording it: whichever
// temp `closeOwnedLetBracket` hands back has already had its placement decided — either it is the
// body's own result temp unchanged (recorded by whatever lowered it), a copy-out's temp (recorded
// by `closeOwnedLetBracket` itself, the one case that is a genuinely new physical value), or a
// spilled reload's temp (deliberately left unrecorded, since a reload is the same reference at a
// new address, not a new value).
let finishClosedLetResult finalTemp resultType state = LoweredCoreValue(state = state, temp = finalTemp, semanticType = resultType, error = None)

// A unification failure is found at the innermost enclosing span, as stage 0 reports it.
let mismatchSiteOf (state: CoreLoweringState) =
    CoreMismatchSite(
        span = state.currentSpan,
        location = currentLocation(state),
        argument = None
    )

let bindType left right state =
    match state with
        | CoreLoweringState { substitution = existing } ->
            match right
            |> resolveType(state)
            |> unify(resolveType(state)(left)) with
                | UnificationResult { substitution = added, error = None } ->
                    match added with
                        | [] -> (state, None)
                        | _ ->
                            (withSubstitution(extendSubstitutionMap(added)(existing))(state), None)
                | UnificationResult { error = Some(error) } ->
                    (state, state
                    |> mismatchSiteOf
                    |> CoreCallTypeMismatch(error)
                    |> Some)

// The type the context expects of the next expression lowered, threaded through the state:
// `lowerCore` consumes it. A let, recursive binding, lambda, if, match, handle, call, list
// literal, or cons forwards it to the parts stage 0 forwards it to; every other expression is
// lowered without it and unified with it afterwards. The result state never carries one.
let consumerRequestOf (state: CoreLoweringState) = state.consumerRequest

// Whether a request asks for nothing at all, which is what most expressions are lowered under.
let isEmptyConsumerRequest (request: ConsumerRequest) =
    match request with
        | ConsumerRequest { expectedType = None, argumentSite = None, runtimeString = false, runtimeAdt = false, runtimeRecord = false, runtimeList = false, runtimeTuple = false, transferSlot = None, tailPosition = false, transfersRuntimeManagedChildren = false, tailCall = false, consTailPosition = false, runtimeTcoListTailSlot = None, recordFieldCell = false } -> true
        | _ -> false

// Replacing a request that asks for nothing with another such request leaves the state as it is.
// The state is a record of over a hundred fields, and this copy was the most frequent allocation
// lowering made.
let withConsumerRequest (request: ConsumerRequest) (state: CoreLoweringState) =
    if isEmptyConsumerRequest(request) && isEmptyConsumerRequest(state.consumerRequest)
    then state
    else state with consumerRequest = request

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

let withLoweredConsumerRequest (request: ConsumerRequest) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = loweredState } ->
            if isEmptyConsumerRequest(request) && isEmptyConsumerRequest(loweredState.consumerRequest)
            then lowered
            else lowered with state = withConsumerRequest(request)(loweredState)

let runtimeStringRequested (state: CoreLoweringState) =
    match consumerRequestOf(state) with
        | ConsumerRequest { runtimeString = requested } -> requested

let transfersChildrenRequested (state: CoreLoweringState) =
    match consumerRequestOf(state) with
        | ConsumerRequest { transfersRuntimeManagedChildren = requested } -> requested

// A call argument's request: the callee's parameter type, and the transfer when the call is the
// loop's tail self-call; any other call's argument is marked as one.
let withArgumentRequest expected (site: Maybe(CoreMismatchSite)) (transfers: Bool) (callArgument: Bool) (state: CoreLoweringState) = withConsumerRequest((emptyConsumerRequest with expectedType = expected, argumentSite = site, transfersRuntimeManagedChildren = transfers, callArgument = callArgument))(state)

// The reference-counted heap temps of the current function.
let runtimeTempStateOf (temp: Int) (state: CoreLoweringState) = Ashes.Collection.Map.getInt(temp)(state.runtimeTemps)

let isRuntimeTemp (temp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(temp)(state) with
        | Some(_runtimeState) -> true
        | None -> false

// Whether a temp is defined by a reference-counted list copy-out: the copy is fresh, or a retain of
// a list already reference-counted, so the temp holds a reference of its own either way.
let recursive definedByRuntimeListCopyOut (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = CopyOutList(destTemp, _source, _headCopy, runtimeManaged, _purpose) } :: rest ->
            if destTemp == temp
            then runtimeManaged
            else definedByRuntimeListCopyOut(temp)(rest)
        | _ :: rest -> definedByRuntimeListCopyOut(temp)(rest)

// Stage 0's exit test in `TcoExitResultMayBeParameter`: a join that holds a reference of its own
// is never one of the parameters' own references, though it may be the very value a parameter
// holds, so the exit releases every parameter instead of transferring one. Stage 0 also exempts a
// result its facts call newly produced; a runtime temp marked newly produced here can still be a
// parameter handed on through a join, so this lowering does not. A result the scope copied out to
// the reference-counted heap holds its own reference too: where stage 0 retains in the arm, this
// lowering borrows there and the copy-out retains.
let exitResultOwnsItsReference (temp: Int) (state: CoreLoweringState) = isOwnedReferenceJoin(temp)(state) || isRetainedArmJoin(temp)(state) || definedByRuntimeListCopyOut(temp)(state.reversedInstructions)

let markRuntimeTemp (temp: Int) (runtimeState: RuntimeTempState) (state: CoreLoweringState) = state with runtimeTemps = Ashes.Collection.Map.setInt(temp)(runtimeState)(state.runtimeTemps)

let markLoweredRuntimeTemp (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state, temp = temp } -> lowered with state = markRuntimeTemp(temp)(RuntimeNewlyProduced)(state)

// Whether a lifted function's body result is a reference-counted heap value, recorded when the
// body is finished and read back by the closure carrying the function and by known calls to it.
let bodyReturnsRuntimeManaged (label: Str) (state: CoreLoweringState) =
    match state
    |> stateBodyRuntimeManagedByLabel
    |> Ashes.Collection.Map.getStr(label) with
        | Some(runtimeManaged) -> runtimeManaged
        | None -> false

let recordBodyRuntimeManaged (label: Str) (runtimeManaged: Bool) (state: CoreLoweringState) =
    withStateBodyRuntimeManagedByLabel(state
    |> stateBodyRuntimeManagedByLabel
    |> Ashes.Collection.Map.setStr(label)(runtimeManaged))(state)

// Stage 0's `BackfillSelfClosureResultOwnership`: a recursive function's own call sites build its
// callee closure from `CoreSelf` while the body is still being lowered, before this body's own
// runtime-managed verdict exists, so every self-closure was emitted with the returns bit clear —
// its call site then always takes the arena copy-out branch, one copy of the whole result per
// recursion level for a non-tail recursive producer. The bit is a compile-time constant in an
// instruction this function already owns, so once the verdict is known it is written back into
// the not-yet-frozen instruction buffer, before `finishLiftedFunction` freezes it. Scoped to this
// same function's own body, exactly as stage 0's own backfill is: a self-call reached only
// through a nested closure helper's separate function is out of scope for both.
let recursive backfillSelfClosureInstructions (label: Str) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = MakeClosure(target, funcLabel, environmentTemp, environmentSize, runtimeManaged, returnsRuntimeManaged, acceptsRuntimeManagedArgument, returnsGeneralRcOwned), location = location } as instruction) :: rest ->
            if funcLabel == label && returnsRuntimeManaged == false
            then IrInstruction(instruction = MakeClosure(target)(funcLabel)(environmentTemp)(environmentSize)(runtimeManaged)(true)(acceptsRuntimeManagedArgument)(returnsGeneralRcOwned), location = location) :: backfillSelfClosureInstructions(label)(rest)
            else instruction :: backfillSelfClosureInstructions(label)(rest)
        | instruction :: rest -> instruction :: backfillSelfClosureInstructions(label)(rest)

// Also reaches a reuse specialization's own self-call, generated through this same recursive-body
// finishing path (the "one-member recursive group" that redirects the specialization's recursion
// into itself). Stage 0 skips a returns-bit check there entirely — its dedicated specialization
// lowering (`LowerReuseSpecializedCall`) never opens a call window around that self-call at all,
// since the result is consumed directly by the rebuild regardless of representation — a bypass
// this lowering does not have yet (its specialization body reuses the SAME general call-window
// path an ordinary call takes). Measured (`makeList`/`doubleAll` reuse over 20000 elements): the
// window that path opens costs real work per level either way; leaving this backfill unconditional
// gives the window's own runtime branch a bit it can prove always takes the reclaim side, which
// measures far better (28.1 GB baseline to 9.4 GB) than gating it off to match stage 0's own
// instruction shape structurally (18.7 GB) — until that dedicated bypass is ported, this is the
// better of the two available answers, not the final one.
// A member of a recursive group is captured into its siblings' environments by their own outermost
// stages, lowered before this body, so the closures in those finished functions are written back too.
let recursive backfillFinishedFunctions (label: Str) (functions: List(IrFunction)) =
    match functions with
        | [] -> []
        | (IrFunction { label = finishedLabel, instructions = instructions } as function) :: rest ->
            if Ashes.Text.startsWith(finishedLabel)("recgroup_")
            then (function with instructions = backfillSelfClosureInstructions(label)(instructions)) :: backfillFinishedFunctions(label)(rest)
            else function :: backfillFinishedFunctions(label)(rest)

let backfillSelfClosureResultOwnership (label: Str) (runtimeManaged: Bool) (state: CoreLoweringState) =
    if runtimeManaged
    then
        (state with reversedInstructions = backfillSelfClosureInstructions(label)(state.reversedInstructions)) |> (given (current: CoreLoweringState) ->
            if Ashes.Text.startsWith(label)("recgroup_")
            then current with functions = backfillFinishedFunctions(label)(current.functions)
            else current)
    else state

// The label of the closure a lowered body returns: the last closure instruction that produced
// the body temp, read from the body's instructions in reverse emission order.
let recursive returnedClosureLabelOf (bodyTemp: Int) (reversedInstructions: List(IrInstruction)) =
    match reversedInstructions with
        | [] -> None
        | IrInstruction { instruction = MakeClosure(target, label, _environment, _size, _returns, _accepts, _flag, _returnsOwned) } :: rest ->
            if target == bodyTemp
            then Some(label)
            else returnedClosureLabelOf(bodyTemp)(rest)
        | IrInstruction { instruction = MakeClosureStack(target, label, _environment, _size, _returns, _accepts, _returnsOwned) } :: rest ->
            if target == bodyTemp
            then Some(label)
            else returnedClosureLabelOf(bodyTemp)(rest)
        | _ :: rest -> returnedClosureLabelOf(bodyTemp)(rest)

// Stage 0's `RecordReturnedClosureLabel`: a curried function's stage is remembered under the
// function's label, so a saturated spine can be followed to the innermost body it reaches.
let recordReturnedClosureLabel (label: Str) (bodyTemp: Int) (reversedInstructions: List(IrInstruction)) (state: CoreLoweringState) =
    match returnedClosureLabelOf(bodyTemp)(reversedInstructions) with
        | Some(returned) ->
            withStateFunctionReturnedClosureLabels(state
            |> stateFunctionReturnedClosureLabels
            |> Ashes.Collection.Map.setStr(label)(returned))(state)
        | None -> state

// Whether a lifted function normalizes its argument into an owned value at entry, so its closure
// advertises that it accepts a runtime-managed argument and a caller hands over a retained
// reference.
let recursive containsLabel (label: Str) (labels: List(Str)) =
    match labels with
        | [] -> false
        | candidate :: rest -> candidate == label || containsLabel(label)(rest)

let acceptsRuntimeManagedArgument (label: Str) (state: CoreLoweringState) =
    state
    |> stateRuntimeNormalizedArgumentLabels
    |> containsLabel(label)

let recordRuntimeNormalizedArgument (label: Str) (state: CoreLoweringState) = withStateRuntimeNormalizedArgumentLabels(label :: stateRuntimeNormalizedArgumentLabels(state))(state)

// Stage 0's `ReleaseConsumedOwnedOperand`: a consumer that keeps nothing of a newly produced
// reference-counted value (a string, or a byte buffer) releases it right after the use.
let releaseConsumedOperandOf (typeName: Str) (temp: Int) (state: CoreLoweringState) =
    match runtimeTempStateOf(temp)(state) with
        | Some(RuntimeNewlyProduced) ->
            state
            |> emit(RcDrop(temp)(typeName)(-1)(true)(false)(None))
            |> markRuntimeTemp(temp)(RuntimeTransferred)
        | _ -> state

let releaseConsumedOperand (temp: Int) (state: CoreLoweringState) = releaseConsumedOperandOf("String")(temp)(state)

let unifyExpectedResult expected lowered =
    match lowered with
        | LoweredCoreValue { error = Some(_error) } -> lowered
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            match bindType(expected)(semanticType)(state) with
                | (failedState, Some(error)) -> failure(failedState)(error)
                | (typedState, None) ->
                    success(temp)(resolveType(typedState)(semanticType))(typedState)

// A mismatch an expected type found while lowering a call argument is reported at the argument's
// call; a mismatch already located, or found outside an argument, keeps its own site.
let locateLoweredMismatch (site: Maybe(CoreMismatchSite)) (lowered: LoweredCoreValue) =
    match (site, lowered) with
        | (Some(argumentSite), LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = Some(CoreCallTypeMismatch(error, CoreMismatchSite { argument = None })) }) ->
            LoweredCoreValue(
                state = state,
                temp = temp,
                semanticType = semanticType,
                error = argumentSite
                |> CoreCallTypeMismatch(error)
                |> Some
            )
        | _ -> lowered

let argumentSiteOf (state: CoreLoweringState) =
    match consumerRequestOf(state) with
        | ConsumerRequest { argumentSite = site } -> site
