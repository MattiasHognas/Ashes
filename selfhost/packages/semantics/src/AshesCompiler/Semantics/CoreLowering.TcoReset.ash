// Tail-call back-edge arguments: affine reservations, slot drops, successor retains, compaction
// copies and the arena reset. A part of `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.ArgumentTypeContainment.mayContainArgumentType
import AshesCompiler.Semantics.ArgumentTypeContainment.mayContainAnyArgumentType
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.CallOwnership
import AshesCompiler.Semantics.ConstructionRetains
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.StateMachineTransform.getDefinedTemps
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.StructuralDroppers
import AshesCompiler.Semantics.StructuralCopiers
import AshesCompiler.Semantics.TcoRuntimeManagedParams
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.CopyPlans
export (
    value lookupRuntimeManagedAdtSlot,
    value isTcoBackEdgeArgPassThrough,
    value parameterNameAt,
    value affineAppendContextFor,
    value affineReservationFor,
    value lookupManagedStrSlot,
    value accumulatorRouteShape,
    value placesTcoListElement,
    value containsUnresolvedLayout,
    value anyUnresolvedLayout,
    value callCopyOutOfIndexed,
    value isGeneralRcValueTypeIndexed,
    value callCopyOutOf,
    value isGeneralRcValueType,
    value tcoListCopyPlanOf,
    value isCallResultAccumulatorElement,
    value restFlags,
    value replaceTypeVariables,
    value constructorResultNameIs,
    value typeVariablePairs,
    value structuralComponentTypes,
    value namedTypeFieldTypesAs,
    value namedTypeDeclaredFields,
    value containsBytesLayout,
    value isResourceHandle,
    value consumedScalarListTail,
    value spliceInlineReleaseWith,
    value emitGuardedListRelease,
    value emitOwnedValueRelease,
    value emitGuardedAdtRelease,
    value tcoAdtCopyPlanOf,
    value ownedChildrenOfNamed,
    value childCopyPlanOf,
    value emitChildReferenceOrCopy,
    value insertClosureNormalizers,
    value emitPrunedClosure,
    value emitPrunedClosureIn,
    value isFreshAggregateLiteral,
    value registerGeneralRcOwnedSlot,
    value saveGeneralRcOwnedPredecessor,
    value registerGeneralRcOwnedPredecessor,
    value predecessorOwnerOf,
    value slotBindersOf,
    value emitOwnedValueChildDrop,
    value emitGeneralRcOwnedSlotDropsOf,
    value emitGeneralRcOwnedSlotDrops,
    value typeMayContainArgumentType,
    value callResultMayContainArgumentType,
    value callResultMayContainAnyArgumentType,
    value resultTypeMayHoldPartOf,
    value closureNormalizerCopiesCaptures,
    value ownedSlotsOutside,
    value ownedSlotsOutsideResult,
    value typeAtIndex,
    value emitHeldRetainReleases,
    value startsListAfresh,
    value isPredecessorSlot,
    value emitSkipUnlessReferenceCounted,
    value emitSkipUnlessPresent,
    value freshListDropLabels,
    value emitListDropTest,
    value emitUniqueCellTest,
    value emitListDropAdvance,
    value emitListDropSharedExit,
    value emitRuntimeListSpineDrop,
    value takenOverConsumerAccepts,
    value isTakeOverCandidateList,
    value noteSpineFreedSlot,
    value emitTcoDeepCopy,
    value emitLocatedDeepCopy,
    value emitArenaCopyOfReferenceCounted,
    value storesArenaCopyForKeptSuccessor,
    value keptSuccessorTransferRetain,
    value retainSuccessorTransferredChild,
    value isTransferRetainIn,
    value emitResolvedTcoReset,
)

// One argument position of a back edge as the runtime-managed reset sees it: the parameter it
// feeds, the successor value and the parameter's old value, whether the successor was produced
// on the reference-counted heap, its self-call shape, and the active flag and element type of a
// runtime-managed list slot.
type TcoResetArgument =
    | ordinal: Int
    | parameterSlot: Int
    | argumentType: SemanticType
    | argumentTemp: Int
    | oldTemp: Int
    | argumentRuntime: Bool
    | shape: TcoArgumentShape
    | managedList: Maybe((Int, SemanticType))
    | managedAdt: Maybe((Int, SemanticType, Str))
    | managedStr: Maybe(Int)
    | argumentExpression: Expr
    | singleFreshCons: Bool
    | passesThrough: Bool

// The active flag, resolved type and type name of a runtime-managed ADT slot.
let recursive lookupRuntimeManagedAdtSlot (slot: Int) (entries: List((Int, Int, SemanticType, Str))) =
    match entries with
        | [] -> None
        | (candidate, activeSlot, semanticType, typeName) :: rest ->
            if candidate == slot
            then Some((activeSlot, semanticType, typeName))
            else lookupRuntimeManagedAdtSlot(slot)(rest)

// Whether a tail self-call's argument expression is exactly the same parameter's own value,
// unchanged — a pass-through the back edge neither retains nor releases.
let isTcoBackEdgeArgPassThrough (argument: Expr) (slot: Int) (state: CoreLoweringState) =
    match unspanArgument(argument) with
        | ExprVar(name) ->
            match lookupBinding(name)(state.bindings) with
                | Some(CoreBinding { location = CoreLocal(boundSlot) }) -> boundSlot == slot
                | _ -> false
        | _ -> false

// The leftmost leaf of a `+` chain, when it is a plain variable.
let recursive affineLeafName (expression: Expr) =
    match unspanArgument(expression) with
        | ExprAdd(left, _right) -> affineLeafName(left)
        | ExprVar(name) -> Some(name)
        | _ -> None

let recursive parameterNameAt (ordinal: Int) (names: List(Str)) =
    match names with
        | [] -> None
        | name :: rest ->
            if ordinal == 0
            then Some(name)
            else parameterNameAt(ordinal - 1)(rest)

let recursive lookupAffineReservation (slot: Int) (reservations: List((Int, Int, Int))) =
    match reservations with
        | [] -> None
        | (candidate, reservationStart, reservationEnd) :: rest ->
            if candidate == slot
            then Some((reservationStart, reservationEnd))
            else lookupAffineReservation(slot)(rest)

// Stage 0's `LowerCallTcoArmAffineStringArg`: a tail self-call argument at an affine ordinal
// that is a `+` chain rooted at the parameter itself arms the in-place append for the chain,
// naming the parameter, its slot, and the loop's reservation slots for it.
let affineAppendContextFor (argument: Expr) (ordinal: Int) (slot: Maybe(Int)) (loop: CoreTcoLoop) (reservations: List((Int, Int, Int))) =
    match (slot, unspanArgument(argument)) with
        | (Some(parameterSlot), ExprAdd(_left, _right)) ->
            if containsInt(ordinal)(loop.affineOrdinals)
            then
                match (affineLeafName(argument), parameterNameAt(ordinal)(loop.parameterNames), lookupAffineReservation(parameterSlot)(reservations)) with
                    | (Some(leaf), Some(name), Some((reservationStart, reservationEnd))) ->
                        if leaf == name
                        then Some((name, parameterSlot, reservationStart, reservationEnd))
                        else None
                    | _ -> None
            else None
        | _ -> None

// Stage 0's `ResolveAffineAppendReservation`: the reservation slots of a `+` whose leftmost leaf
// is the armed accumulator, bound to the armed slot (a shadowing binding of the same name is
// not the accumulator).
let affineReservationFor (left: Expr) (state: CoreLoweringState) =
    match stateAffineAppendContext(state) with
        | None -> None
        | Some((name, slot, reservationStart, reservationEnd)) ->
            match affineLeafName(left) with
                | Some(leaf) ->
                    if leaf == name && isTcoBackEdgeArgPassThrough(ExprVar(leaf))(slot)(state)
                    then Some((reservationStart, reservationEnd))
                    else None
                | None -> None

let recursive lookupManagedStrSlot (slot: Int) (entries: List((Int, Int))) =
    match entries with
        | [] -> None
        | (candidate, activeSlot) :: rest ->
            if candidate == slot
            then Some(activeSlot)
            else lookupManagedStrSlot(slot)(rest)

// A successor's runtime placement is read again once the frame's placements are final (stage 0's
// `TcoBackEdgeRefreshRuntimeManagedParams`, for every argument): the affine append promoted at
// finalization produced a `Str` on the reference-counted heap after the back edge was scheduled,
// and the pattern-owner duplicate a placed pattern root promotes is a real retain from then on.
let refreshedArgumentRuntime (temp: Int) (scheduled: Bool) (state: CoreLoweringState) = scheduled || isRuntimeTemp(temp)(state)

// A position the accumulator-rebuild route may place: one no single shape describes, or a list
// handed on and replaced by a call's result, whose own route places only strings and scalars.
let accumulatorRouteShape (shape: TcoArgumentShape) = !isTcoListShape(shape) || shape == TcoRefilledFreshListShape

// Stage 0's `HasBoundedCopy`: a value of the type is a bounded graph, no list and no recursive
// type, so copying it on every iteration costs the same each time rather than growing with what
// the loop builds.
let recursive hasBoundedCopy (semanticType: SemanticType) (path: List(SemanticType)) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemList(_element) -> false
        | SemTuple(elements) -> allBoundedCopies(elements)(path)(state)
        | SemNamed(_symbolId, _name, _arguments) as named ->
            if containsType(named)(path)
            then false
            else
                allBoundedCopies(heapChildTypesOfNamed(named)(state))(named :: path)(state)
        | _ -> true
and allBoundedCopies (types: List(SemanticType)) (path: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> true
        | semanticType :: rest -> hasBoundedCopy(semanticType)(path)(state) && allBoundedCopies(rest)(path)(state)

// Whether a loop can place a list of this element on the reference-counted heap, as
// `tcoListElementSupported` answers.
let placesTcoListElement (element: SemanticType) (state: CoreLoweringState) =
    match heapFactsOf(element)(state) with
        | HeapLayoutFacts { runtimeTcoListElementSupported = supported } -> supported

// Stage 0's `ContainsUnresolvedLayoutType`: a type variable or type parameter anywhere in the
// value's layout.
let recursive containsUnresolvedLayout (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemVariable(_id) -> true
        | SemParameter(_id, _name) -> true
        | SemList(element) -> containsUnresolvedLayout(element)(state)
        | SemTuple(elements) -> anyUnresolvedLayout(elements)(state)
        | SemNamed(_symbolId, _name, arguments) -> anyUnresolvedLayout(arguments)(state)
        | _ -> false
and anyUnresolvedLayout (types: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> false
        | semanticType :: rest -> containsUnresolvedLayout(semanticType)(state) || anyUnresolvedLayout(rest)(state)

// Stage 0's `GetCallCopyOutKind`: the scope copy-outs plus the list results whose elements are
// strings or scalar lists.
// Stage 0's `IsGeneralRcValueType`: a resolved type that reaches a named type only the
// normalization helper expresses and has no fixed copy-out. A value of it follows the contract:
// passed borrowed, returned owned. A reuse specialization keeps its values in the persistent
// region and takes no part in it, nor does a program where a function may run under a
// live handler post.
// Stage 0's `IsCallResultAccumulatorElement`: an accumulator a self-call replaces with a call's
// result holds the contract's values (resolved, as stage 0's `IsGeneralRcValueType` asks) or any
// other heap element the loop can place, a list inside it included: its arena cells would otherwise
// keep values the iteration owns with no owner left to release them. An arena sibling that may
// receive such an inner list blocks the accumulator (`siblingReceivesAccumulatorPart`).
let callCopyOutOfIndexed (semanticType: SemanticType) (state: CoreLoweringState) =
    match scopeCopyOutOfIndexed(semanticType)(state) with
        | (Some(ShallowScopeCopyOut(staticSizeBytes)), queried) -> (Some(ShallowCallCopyOut(staticSizeBytes)), queried)
        | (Some(ListScopeCopyOut), queried) -> (Some(ListCallCopyOut(InlineListHead)), queried)
        | (None, queried) ->
            match resolveType(queried)(semanticType) with
                | SemList(element) ->
                    match element
                    |> resolveType(queried)
                    |> listHeadCopyOf with
                        | Some(headCopy) -> (Some(ListCallCopyOut(headCopy)), queried)
                        | None -> (None, queried)
                | _ -> (None, queried)

let isGeneralRcValueTypeIndexed (semanticType: SemanticType) (state: CoreLoweringState) =
    match callCopyOutOfIndexed(semanticType)(state) with
        | (copyOut, queried) ->
            match (stateSpecializationFreshInputs(queried), copyOut) with
                | (None, None) ->
                    if programMayRunUnderLiveHandlerPost(queried) == false && !resultSurvivesReset(semanticType)(queried)
                    then
                        match isGeneralRcAdmissibleIndexed(semanticType)([])(queried) with
                            | (false, classified) -> (false, classified)
                            | (true, classified) -> reachesGeneralRcNamedTypeIndexed(semanticType)([])(classified)
                    else (false, queried)
                | _ -> (false, queried)

let callCopyOutOf semanticType state =
    match callCopyOutOfIndexed(semanticType)(state) with
        | (answer, _queried) -> answer

let isGeneralRcValueType semanticType state =
    match isGeneralRcValueTypeIndexed(semanticType)(state) with
        | (answer, _queried) -> answer

// The copy of a managed list into the reference-counted heap. A list of the contract's elements
// (stage 0's `IsGeneralRcTcoListElement`, an accumulator of call results) copies its heads through
// their normalization helper. Any other list has the inline copies, and one they do not express
// whose element reaches the contract's types through a tuple or a list (stage 0's
// `IsGeneralRcValueType`, a list of keyed call results) has the general copy, its heads' parts
// normalized at any depth. A list with neither cannot be placed at all.
let managedListCopyPlanOf (listType: SemanticType) (element: SemanticType) (state: CoreLoweringState) =
    if isGeneralRcNamedType(element)(state)
    then generalCopyPlanOf(listType)(state)
    else
        match argumentCopyPlanOf(listType)(state) with
            | Some(plan) -> Some(plan)
            | None ->
                if isGeneralRcValueType(element)(state)
                then generalCopyPlanOf(listType)(state)
                else None

// The copy of a list loop parameter into the reference-counted heap, which is also what decides
// whether it can be placed at all.
let tcoListCopyPlanOf (element: SemanticType) (state: CoreLoweringState) = managedListCopyPlanOf(SemList(element))(element)(state)

let isCallResultAccumulatorElement (element: SemanticType) (state: CoreLoweringState) = isGeneralRcValueType(element)(state) && !containsUnresolvedLayout(element)(state) || !resultSurvivesReset(element)(state) && placesTcoListElement(element)(state)

// A list placed on the reference-counted heap as an accumulator rebuild is normalized and released
// at a back edge like a choice accumulator: its reset argument carries that shape.
let resetArgumentShape (shape: TcoArgumentShape) (ordinal: Int) (managedList: Maybe((Int, SemanticType))) (state: CoreLoweringState) =
    match (managedList, stateTcoLoop(state)) with
        | (Some((_activeSlot, elementType)), Some(loop)) ->
            if accumulatorRouteShape(shape) && isCallResultAccumulatorElement(elementType)(state) && containsInt(ordinal)(loop.accumulatorRebuildOrdinals)
            then TcoAccumulatorRebuildShape
            else shape
        | _ -> shape

let firstFlag (flags: List(Bool)) =
    match flags with
        | flag :: _rest -> flag
        | [] -> false

let restFlags (flags: List(Bool)) =
    match flags with
        | _flag :: rest -> rest
        | [] -> []

let recursive tcoResetArguments (ordinal: Int) (slots: List(Int)) (types: List(SemanticType)) (temps: List(Int)) (oldTemps: List(Int)) (runtime: List(Bool)) (shapes: List(TcoArgumentShape)) (expressions: List(Expr)) (freshConses: List(Bool)) (passThroughs: List(Bool)) (managed: List((Int, Int, SemanticType))) (managedAdts: List((Int, Int, SemanticType, Str))) (managedStrs: List((Int, Int))) (state: CoreLoweringState) =
    match (slots, types, temps, oldTemps, runtime, shapes, expressions) with
        | (slot :: restSlots, argumentType :: restTypes, temp :: restTemps, oldTemp :: restOld, isRuntime :: restRuntime, shape :: restShapes, expression :: restExpressions) ->
            TcoResetArgument(ordinal = ordinal, parameterSlot = slot, argumentType = argumentType, argumentTemp = temp, oldTemp = oldTemp, argumentRuntime = refreshedArgumentRuntime(temp)(isRuntime)(state), shape = resetArgumentShape(shape)(ordinal)(lookupRuntimeManagedListSlot(slot)(managed))(state), managedList = lookupRuntimeManagedListSlot(slot)(managed), managedAdt = lookupRuntimeManagedAdtSlot(slot)(managedAdts), managedStr = lookupManagedStrSlot(slot)(managedStrs), argumentExpression = expression, singleFreshCons = firstFlag(freshConses), passesThrough = firstFlag(passThroughs)) :: tcoResetArguments(ordinal + 1)(restSlots)(restTypes)(restTemps)(restOld)(restRuntime)(restShapes)(restExpressions)(restFlags(freshConses))(restFlags(passThroughs))(managed)(managedAdts)(managedStrs)(state)
        | _ -> []

let recursive lookupTypeVariablePair (variableId: Int) (pairs: List((Int, SemanticType))) =
    match pairs with
        | [] -> None
        | (candidate, replacement) :: rest ->
            if candidate == variableId
            then Some(replacement)
            else lookupTypeVariablePair(variableId)(rest)

// A type with the given variables replaced; a function type is kept as it is, since a function
// value answers "may contain" whatever its types.
let recursive replaceTypeVariables (pairs: List((Int, SemanticType))) (semanticType: SemanticType) =
    match semanticType with
        | SemVariable(variableId) ->
            match lookupTypeVariablePair(variableId)(pairs) with
                | Some(replacement) -> replacement
                | None -> semanticType
        | SemList(element) ->
            element
            |> replaceTypeVariables(pairs)
            |> SemList
        | SemTuple(elements) ->
            elements
            |> map(replaceTypeVariables(pairs))
            |> SemTuple
        | SemNamed(symbolId, name, arguments) ->
            arguments
            |> map(replaceTypeVariables(pairs))
            |> SemNamed(symbolId)(name)
        | _ -> semanticType

let constructorResultNameIs (typeName: Str) (layout: CoreConstructorLayout) =
    match constructorResultName(layout) with
        | Some(resultName) -> resultName == typeName
        | None -> false

let recursive typeVariablePairs (variables: List(SemanticType)) (arguments: List(SemanticType)) =
    match (variables, arguments) with
        | (SemVariable(variableId) :: restVariables, argument :: restArguments) -> (variableId, argument) :: typeVariablePairs(restVariables)(restArguments)
        | (_variable :: restVariables, _argument :: restArguments) -> typeVariablePairs(restVariables)(restArguments)
        | _ -> []

// Every constructor field of a named type, resolved, with whether the type has any constructor at
// all: a type the lowering knows no constructor for proves nothing about its own layout.
let recursive namedTypeConstructorFieldTypes (typeName: Str) (layouts: List(CoreConstructorLayout)) (state: CoreLoweringState) (found: Bool) (fieldTypes: List(SemanticType)) =
    match layouts with
        | [] -> (found, fieldTypes)
        | layout :: rest ->
            match constructorResultName(layout) with
                | Some(resultName) ->
                    if resultName == typeName
                    then
                        match layoutFieldTypes(layout)(state) with
                            | (layoutFields, _resultType) ->
                                layoutFields
                                |> map(resolveType(state))
                                |> append(fieldTypes)
                                |> namedTypeConstructorFieldTypes(typeName)(rest)(state)(true)
                    else namedTypeConstructorFieldTypes(typeName)(rest)(state)(found)(fieldTypes)
                | None -> namedTypeConstructorFieldTypes(typeName)(rest)(state)(found)(fieldTypes)

// Stage 0's `InstantiatedFieldType` over every constructor of a named type: each field at the
// instantiation the type arguments give, so a field of the declaration's own type parameter is
// the argument in its place rather than a value of any type.
let recursive namedTypeInstantiatedFieldTypes (typeName: Str) (arguments: List(SemanticType)) (layouts: List(CoreConstructorLayout)) (state: CoreLoweringState) (fieldTypes: List(SemanticType)) =
    match layouts with
        | [] -> fieldTypes
        | layout :: rest ->
            if constructorResultNameIs(typeName)(layout)
            then
                match layoutFieldTypes(layout)(state) with
                    | (layoutFields, SemNamed(_symbolId, _name, variables)) ->
                        layoutFields
                        |> map(arguments
                        |> typeVariablePairs(variables)
                        |> replaceTypeVariables)
                        |> map(resolveType(state))
                        |> append(fieldTypes)
                        |> namedTypeInstantiatedFieldTypes(typeName)(arguments)(rest)(state)
                    | (layoutFields, _resultType) ->
                        layoutFields
                        |> map(resolveType(state))
                        |> append(fieldTypes)
                        |> namedTypeInstantiatedFieldTypes(typeName)(arguments)(rest)(state)
            else namedTypeInstantiatedFieldTypes(typeName)(arguments)(rest)(state)(fieldTypes)

// Stage 0's `StructuralComponentTypes`: what a value of this type structurally contains as far as the
// type itself says. A named type's fields are not among its type arguments, which the walk below
// expands separately.
let structuralComponentTypes (semanticType: SemanticType) =
    match semanticType with
        | SemList(element) -> [element]
        | SemPointer(pointee) -> [pointee]
        | SemTuple(elements) -> elements
        | SemNamed(_id, _name, arguments) -> arguments
        | SemCapability(_name, arguments) -> arguments
        | _ -> []

let namedTypeFieldTypesAs (instantiated: Bool) (name: Str) (arguments: List(SemanticType)) (state: CoreLoweringState) =
    match stateStableConstructorFields(name)(state) with
        | Some(fields) -> fields
        | None ->
            if instantiated
            then
                namedTypeInstantiatedFieldTypes(name)(arguments)(stateConstructorsOfType(name)(state))(state)([])
            else
                match namedTypeConstructorFieldTypes(name)(stateConstructorsOfType(name)(state))(state)(false)([]) with
                    | (_found, fieldTypes) -> fieldTypes

let namedTypeDeclaredFields name (state: CoreLoweringState) =
    match stateStableConstructorFields(name)(state) with
        | Some(fields) -> (true, fields)
        | None ->
            namedTypeConstructorFieldTypes(name)(stateConstructorsOfType(name)(state))(state)(false)([])

// Stage 0's `ContainsBytesLayout`: whether a value of the type may hold Bytes, through its
// elements or its constructors' fields at this instantiation. `path` holds the named types being
// expanded, which stops a recursive type; `done` the argument-free ones already answered no, so a
// type reached along several paths is expanded once. Answers the verdict with the grown `done`.
let recursive bytesLayoutSearch (semanticType: SemanticType) (path: List(Str)) (state: CoreLoweringState) (done: List(Str)) =
    match resolveType(state)(semanticType) with
        | SemBytes -> (true, done)
        | SemList(element) -> bytesLayoutSearch(element)(path)(state)(done)
        | SemTuple(elements) -> anyBytesLayoutSearch(elements)(path)(state)(done)
        | SemNamed(_id, name, arguments) ->
            if containsName(name)(path) || arguments == [] && containsName(name)(done)
            then (false, done)
            else
                match anyBytesLayoutSearch(namedTypeFieldTypesAs(true)(name)(arguments)(state))(name :: path)(state)(done) with
                    | (true, searched) -> (true, searched)
                    | (false, searched) ->
                        if arguments == []
                        then (false, name :: searched)
                        else (false, searched)
        | _ -> (false, done)
and anyBytesLayoutSearch (types: List(SemanticType)) (path: List(Str)) (state: CoreLoweringState) (done: List(Str)) =
    match types with
        | [] -> (false, done)
        | candidate :: rest ->
            match bytesLayoutSearch(candidate)(path)(state)(done) with
                | (true, searched) -> (true, searched)
                | (false, searched) -> anyBytesLayoutSearch(rest)(path)(state)(searched)

let containsBytesLayout (semanticType: SemanticType) (state: CoreLoweringState) =
    match bytesLayoutSearch(semanticType)([])(state)([]) with
        | (found, _done) -> found

// Whether a grown accumulator's back edge normalizes a successor cell left in the arena: a list of
// the contract's elements, or of values holding Bytes, whose cells stage 0 copies at the back edge
// when a head of unknown Bytes origin kept the cell in the arena.
let grownAccumulatorNormalizedAtBackEdge (elementType: SemanticType) (state: CoreLoweringState) = isGeneralRcNamedType(elementType)(state) || containsBytesLayout(elementType)(state)

let isResourceHandle (semanticType: SemanticType) (state: CoreLoweringState) =
    match resourceTypeNameOf(resolveType(state)(semanticType))(state) with
        | Some(_typeName) -> true
        | None -> false

// A consumed tail of a list over scalars is borrowed through the traversal and survives a reset
// on its own (stage 0's `TcoBackEdgeConsumedInlineListTailCanReset`).
let consumedScalarListTail (semanticType: SemanticType) (shape: TcoArgumentShape) (state: CoreLoweringState) =
    match (shape, resolveType(state)(semanticType)) with
        | (TcoConsumedTailShape, SemList(element)) -> resultSurvivesReset(element)(state)
        | _ -> false

let managedListArgumentCanReset (argument: TcoResetArgument) (state: CoreLoweringState) =
    match (argument.managedList, argument.shape) with
        | (Some(_managed), TcoConsumedTailShape) -> true
        | (Some(_managed), TcoPassThroughShape) -> true
        // A list of the contract's elements normalizes whatever the successor is: its new cells are
        // copied, its heads retained, its reference-counted tail shared.
        | (Some((_activeSlot, elementType)), TcoGrownConsShape) -> argument.argumentRuntime || isGeneralRcNamedType(elementType)(state)
        // Any other placed list survives a reset when its successor is built fresh this iteration
        // (stage 0's `FreshListRebuild`): an empty or literal list, a call's result, or a cons
        // chain onto one, which the back edge normalizes onto the reference-counted heap whole.
        | (Some((_activeSlot, elementType)), _shape) -> isGeneralRcNamedType(elementType)(state) || shapeIsFreshList(argument.argumentExpression)
        | _ -> false

// A runtime-managed `Str` slot survives a reset: its successor is the in-place append's
// reference-counted reservation or is copied out to the reference-counted heap before the
// iteration's arena allocations go.
let managedStrArgumentCanReset (argument: TcoResetArgument) =
    match argument.managedStr with
        | Some(_activeSlot) -> true
        | None -> false

// Stage 0's `TcoBackEdgeTryEmitRuntimeManagedReset` admission: every argument survives a reset
// on its own (a scalar, a resource handle), is the parameter's own unchanged value, is a
// consumed tail of a list over scalars, or is carried by a runtime-managed parameter — a `Str`
// accumulator, or a list slot whose successor is a cons cell allocated on the reference-counted
// heap or the pattern-bound tail of its own value.
// A pass-through counts only at a back edge that hands the parameter on unchanged (stage 0's
// `passThrough[i]` needs both the loop-invariant parameter and this call's own argument): a
// back edge that conses onto it, a tail-modulo-cons edge among them, built its successor in the
// iteration's arena.
let tcoResetArgumentCanReset (loop: CoreTcoLoop) (argument: TcoResetArgument) (state: CoreLoweringState) = resultSurvivesReset(argument.argumentType)(state) || isResourceHandle(argument.argumentType)(state) || argument.shape == TcoPassThroughShape && argument.passesThrough || consumedScalarListTail(argument.argumentType)(argument.shape)(state) || managedListArgumentCanReset(argument)(state) || managedStrArgumentCanReset(argument)

let recursive anyManagedListArgument (arguments: List(TcoResetArgument)) =
    match arguments with
        | [] -> false
        | TcoResetArgument { managedList = Some(_managed) } :: _rest -> true
        | TcoResetArgument { managedAdt = Some(_managed) } :: _rest -> true
        | TcoResetArgument { managedStr = Some(_managed) } :: _rest -> true
        | _argument :: rest -> anyManagedListArgument(rest)

// A runtime-managed copy-ADT slot survives a reset: its successor is copied out to the
// reference-counted heap before the iteration's arena allocations go.
let managedAdtArgumentCanReset (argument: TcoResetArgument) =
    match argument.managedAdt with
        | Some(_managed) -> true
        | None -> false

let recursive allResetArgumentsCanReset (loop: CoreTcoLoop) (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match arguments with
        | [] -> true
        | argument :: rest -> (tcoResetArgumentCanReset(loop)(argument)(state) || managedAdtArgumentCanReset(argument)) && allResetArgumentsCanReset(loop)(rest)(state)

let tcoBackEdgeCanEmitRuntimeManagedReset (loop: CoreTcoLoop) (arguments: List(TcoResetArgument)) (state: CoreLoweringState) = anyManagedListArgument(arguments) && allResetArgumentsCanReset(loop)(arguments)(state)

let recursive emitSplicedInstructionsWith emitter (instructions: List(IrInstructionKind)) (state: CoreLoweringState) =
    match instructions with
        | [] -> state
        | instruction :: rest ->
            state
            |> emitter(instruction)
            |> emitSplicedInstructionsWith(emitter)(rest)

let spliceInlineReleaseWith emitter (synthesis: InlineReleaseSynthesis) (state: CoreLoweringState) =
    match synthesis with
        | InlineReleaseSynthesis { instructions = instructions, nextTemp = nextTemp, nextLocal = nextLocal, cache = cache, functions = synthesized, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId } ->
            (state with nextTemp = nextTemp, nextLocal = nextLocal, functions = append(synthesized
            |> map(locateSynthesizedFunction(state))
            |> reverse)(state.functions), nextLambdaId = nextLambdaId, nextLabelId = nextLabelId)
            |> withStateDropperResult(cache)(synthesis.queryContext)
            |> emitSplicedInstructionsWith(emitter)(instructions)

// The inline `rcdrop_list` walk of a runtime-managed list whose cells may be shared, stage 0's
// `EmitRuntimeManagedListDrop`: a unique cell releases its head and continues into its tail, a
// shared cell is only decremented.
let emitInlineListRelease emitter (listTemp: Int) (elementType: SemanticType) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { nextTemp = nextTemp, nextLocal = nextLocal, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
            match synthesizeOwnedAggregateReleaseIndexed(listTemp)(elementType
            |> resolveType(state)
            |> SemList)(OwnedReleasePlan(deepUnique = false, constructorName = None, matchFieldTransfer = None))(dropperTypesOf(state))(cache)(nextTemp)(nextLocal)(lambdaId)(labelId) with
                | (None, queryContext) -> withStateDropperContext(queryContext)(state)
                | (Some(synthesis), _queryContext) -> spliceInlineReleaseWith(emitter)(synthesis)(state)

// The list release under the slot's active flag (stage 0's `EmitRuntimeManagedTcoParamDropIfActive`
// and the guarded predecessor drop): skipped when the slot holds no reference of its own.
let emitGuardedListRelease emitter (labelName: Str) (activeSlot: Int) (listTemp: Int) (elementType: SemanticType) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = activeTemp } ->
            match freshLabel(labelName)(allocated) with
                | FreshLabel { state = labelState, label = skipLabel } ->
                    labelState
                    |> emitter(LoadLocal(activeTemp)(activeSlot))
                    |> emitter(JumpIfFalse(activeTemp)(skipLabel))
                    |> emitInlineListRelease(emitter)(listTemp)(elementType)
                    |> emitter(Label(skipLabel))

let recursive allScalarElements (elements: List(SemanticType)) (state: CoreLoweringState) =
    match elements with
        | [] -> true
        | element :: rest -> resultSurvivesReset(element)(state) && allScalarElements(rest)(state)

// The release of one owned runtime-managed value by its resolved type, stage 0's
// `EmitRuntimeManagedChildDrop`: a string, bytes or big integer is one allocation; a list walks
// its cells; an ADT with owned children tests its cell for uniqueness, releases a unique cell's
// children and drops the cell (`rc_drop_shared`), as does a tuple with owned elements
// (`rc_drop_tuple_shared`); a scalar-field cell and a tuple of scalars drop as one allocation.
let emitOwnedValueRelease emitter (valueTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString ->
            emitter(RcDrop(valueTemp)("String")(-1)(true)(false)(None))(state)
        | SemBytes ->
            emitter(RcDrop(valueTemp)("Bytes")(-1)(true)(false)(None))(state)
        | SemBigInt ->
            emitter(RcDrop(valueTemp)("BigInt")(-1)(true)(false)(None))(state)
        | SemList(element) -> emitInlineListRelease(emitter)(valueTemp)(element)(state)
        | SemNamed(_symbolId, name, _arguments) as named ->
            match state with
                | CoreLoweringState { nextTemp = nextTemp, nextLocal = nextLocal, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
                    match synthesizeOwnedAggregateReleaseIndexed(valueTemp)(named)(OwnedReleasePlan(deepUnique = false, constructorName = None, matchFieldTransfer = None))(dropperTypesOf(state))(cache)(nextTemp)(nextLocal)(lambdaId)(labelId) with
                        | (None, queryContext) ->
                            state
                            |> withStateDropperContext(queryContext)
                            |> emitter(RcDrop(valueTemp)(runtimeManagedAdtTypeName(name))(-1)(true)(false)(None))
                        | (Some(synthesis), _queryContext) -> spliceInlineReleaseWith(emitter)(synthesis)(state)
        | SemTuple(elements) as tuple ->
            if allScalarElements(elements)(state)
            then
                // Stage 0's `EmitRuntimeManagedTupleDrop` allocates its shared label before it
                // knows whether any element needs the unique-cell walk; a tuple of scalars burns
                // the label and drops as one allocation, so the label is allocated here too to
                // keep the numbering.
                match freshLabel("rc_drop_tuple_shared")(state) with
                    | FreshLabel { state = labelled, label = _sharedLabel } ->
                        emitter(RcDrop(valueTemp)("Tuple")(-1)(true)(false)(None))(labelled)
            else
                // A tuple with owned elements releases them from a unique cell before the cell.
                match state with
                    | CoreLoweringState { nextTemp = nextTemp, nextLocal = nextLocal, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
                        spliceInlineReleaseWith(emitter)(synthesizeChildDrop(valueTemp)(tuple)(dropperTypesOf(state))(cache)(nextTemp)(nextLocal)(lambdaId)(labelId))(state)
        | _ -> state

// The deferred root releases of a back edge, unlocated as the rest of its block.
let recursive emitTransferredRootDrops (drops: List((Int, SemanticType))) (state: CoreLoweringState) =
    match drops with
        | [] -> state
        | (rootTemp, rootType) :: rest ->
            state
            |> emitOwnedValueRelease(unlocatedInstruction)(rootTemp)(rootType)
            |> emitTransferredRootDrops(rest)

let recursive slotHasManagedListArgument (slot: Int) (arguments: List(TcoResetArgument)) =
    match arguments with
        | [] -> false
        | TcoResetArgument { parameterSlot = parameterSlot, managedList = Some(_managed) } :: rest -> parameterSlot == slot || slotHasManagedListArgument(slot)(rest)
        | _argument :: rest -> slotHasManagedListArgument(slot)(rest)

let recursive slotHasManagedAdtArgument (slot: Int) (arguments: List(TcoResetArgument)) =
    match arguments with
        | [] -> false
        | TcoResetArgument { parameterSlot = parameterSlot, managedAdt = Some(_managed) } :: rest -> parameterSlot == slot || slotHasManagedAdtArgument(slot)(rest)
        | _argument :: rest -> slotHasManagedAdtArgument(slot)(rest)

let recursive retainsAsRuntimeDuplicate (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = RcDup(target, _source, true, _mayBeEmpty) } :: rest -> target == temp || retainsAsRuntimeDuplicate(temp)(rest)
        | _instruction :: rest -> retainsAsRuntimeDuplicate(temp)(rest)

// The release of a runtime-managed ADT slot's old value under its active flag.
let emitGuardedAdtRelease emitter (labelName: Str) (activeSlot: Int) (cellTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = activeTemp } ->
            match freshLabel(labelName)(allocated) with
                | FreshLabel { state = labelState, label = skipLabel } ->
                    labelState
                    |> emitter(LoadLocal(activeTemp)(activeSlot))
                    |> emitter(JumpIfFalse(activeTemp)(skipLabel))
                    |> emitOwnedValueRelease(emitter)(cellTemp)(semanticType)
                    |> emitter(Label(skipLabel))

// The copy plan of a runtime-managed aggregate loop parameter: a same-arity scalar-field ADT and
// a tuple of scalars copy their cell in one piece, a record with owned children and a tuple with
// owned elements copy their cell and every owned child (stage 0's `EmitRuntimeManagedTcoDeepCopy`).
let tcoAdtCopyPlanOf (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> LeafArgumentCopy(-1)
        | SemBytes -> LeafArgumentCopy(-1)
        | SemList(_element) as list ->
            match argumentCopyPlanOf(list)(state) with
                | Some(plan) -> plan
                | None -> ScalarArgumentCopy
        | SemTuple(elements) as tuple ->
            if allScalarElements(elements)(state)
            then ShallowAdtArgumentCopy(8 * length(elements))
            else
                match argumentCopyPlanOf(tuple)(state) with
                    | Some(plan) -> plan
                    | None -> ShallowAdtArgumentCopy(8 * length(elements))
        | SemNamed(_symbolId, name, _arguments) as named ->
            match (generalCopyPlanOf(named)(state), argumentCopyPlanOf(named)(state)) with
                | (Some(NormalizerArgumentCopy(normalized)), _plan) -> NormalizerArgumentCopy(normalized)
                | (_general, Some(ConstructorArgumentCopy(constructorPlan))) -> ConstructorArgumentCopy(constructorPlan)
                | (_general, Some(SwitchArgumentCopy(plans))) -> SwitchArgumentCopy(plans)
                | _ ->
                    state
                    |> shallowAdtCopySizeBytes(name)
                    |> ShallowAdtArgumentCopy
        | _ -> ScalarArgumentCopy

// The owned children of a single-constructor type, by field index and type.
let ownedChildrenOfNamed (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemNamed(_symbolId, name, _arguments) as named ->
            match state |> stateConstructorsOfType(name) with
                | CoreConstructorLayout { name = constructorName } :: [] ->
                    match heapFactsOf(named)(state) with
                        | HeapLayoutFacts { children = children } -> ownedConstructorChildren(constructorName)(children)
                | _ -> []
        | _ -> []

let childCopyPlanOf (childType: SemanticType) (state: CoreLoweringState) =
    match argumentCopyPlanOf(childType)(state) with
        | Some(plan) -> plan
        | None -> ScalarArgumentCopy

// A back edge's instructions carry no location: the copy walk runs with the current span
// cleared, and the span is put back afterwards.
let unlocatedDeepCopy (sourceTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match emitArgumentDeepCopy(sourceTemp)(plan)((state with currentSpan = None)) with
        | (copied, copyTemp) -> ((copied with currentSpan = state.currentSpan), copyTemp)

// The child a copied aggregate stores, stage 0's guard on `EmitRuntimeManagedTcoDeepCopy`: a
// reference to the original child when the runtime answers that it is already reference-counted,
// and the copy otherwise. A scalar child is its own word and has no representation to ask about.
let emitChildReferenceOrCopy (childTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match (canTestRepresentation(state), plan) with
        | (false, _plan) -> unlocatedDeepCopy(childTemp)(plan)(state)
        | (_tests, ScalarArgumentCopy) -> unlocatedDeepCopy(childTemp)(plan)(state)
        | (_tests, _plan) ->
            emitReferenceOrCopy(childTemp)(unlocatedDeepCopy(childTemp)(plan))(state)

// The text of a capture's type in the normalizer's layout key. Tuples have none yet, so a closure
// capturing one gets no normalizer, as a closure capturing a function never does.
let recursive captureTypeText (semanticType: SemanticType) =
    match semanticType with
        | SemString -> Some("Str")
        | SemBytes -> Some("Bytes")
        | SemBigInt -> Some("BigInt")
        | SemList(element) ->
            match captureTypeText(element) with
                | Some(text) -> Some("List(" + text + ")")
                | None -> None
        | SemNamed(_symbolId, name, _arguments) -> Some(name)
        | scalar -> scalarCaptureText(scalar)

// The copies of a normalizer whose captures are all scalar words: capture `i` is read from the
// source environment (temp 0) at its offset and stored into the target environment (temp 1) at
// the same offset, on temps from `temp` upward; answers the instructions and the next free temp.
let recursive wordNormalizerCopies (layout: List((Int, Str))) (temp: Int) (location: Maybe(IrSourceLocation)) =
    match layout with
        | [] -> ([], temp)
        | (offset, _text) :: rest ->
            match wordNormalizerCopies(rest)(temp + 1)(location) with
                | (instructions, nextTemp) ->
                    (locatedInstruction(location)(LoadMemOffset(temp)(0)(offset)) :: locatedInstruction(location)(StoreMemOffset(1)(offset)(temp)) :: instructions, nextTemp)

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

// Stage 0's `lambda$env_normalize` helper for a closure whose captures all resolved to scalars only
// at program finalization: called with the source environment in slot 0 and the target environment
// in slot 1, it copies every capture's word across and returns 0, the captures owning nothing.
let wordClosureNormalizerFunction (closureLabel: Str) (closureOrigin: IrFunctionOrigin) (layout: List((Int, Str))) (location: Maybe(IrSourceLocation)) =
    match wordNormalizerCopies(layout)(2)(location) with
        | (copies, resultTemp) ->
            IrFunction(
                label = closureLabel + "$env_normalize",
                instructions = append(locatedInstruction(location)(LoadLocal(0)(0)) :: locatedInstruction(location)(LoadLocal(1)(1)) :: copies)([0
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
                |> closureNormalizerOrigin(closureLabel + "$env_normalize")(closureLabel)(closureOrigin)
                |> Some,
                lifetimesPlaced = false
            )

// The release of one owned capture inside the closure dropper, stage 0's
// `EmitRuntimeManagedClosureCaptureDrop`: a list over scalars walks its spine through a cursor
// slot, a string, bytes, or big integer drops as one allocation.
let emitClosureCaptureDrop (capturedTemp: Int) (resolved: SemanticType) (state: CoreLoweringState) =
    match resolved with
        | SemList(_element) ->
            match freshLocal(state) with
                | FreshLocal { state = cursorState, local = cursorSlot } ->
                    match freshLabel("rcdrop_list")(cursorState) with
                        | FreshLabel { state = loopState, label = loopLabel } ->
                            match freshLabel("rcdrop_list_shared")(loopState) with
                                | FreshLabel { state = sharedState, label = sharedLabel } ->
                                    match freshLabel("rcdrop_list_end")(sharedState) with
                                        | FreshLabel { state = endState, label = endLabel } ->
                                            match freshTempRun(5)(endState) with
                                                | FreshTemp { state = walkState, temp = firstTemp } ->
                                                    walkState
                                                    |> emit(StoreLocal(cursorSlot)(capturedTemp))
                                                    |> emitListSpineWalkBody(cursorSlot)(loopLabel)(sharedLabel)(endLabel)(firstTemp)
        | SemString ->
            emit(RcDrop(capturedTemp)("String")(-1)(true)(false)(None))(state)
        | SemBytes ->
            emit(RcDrop(capturedTemp)("Bytes")(-1)(true)(false)(None))(state)
        | SemBigInt ->
            emit(RcDrop(capturedTemp)("BigInt")(-1)(true)(false)(None))(state)
        | SemFunction(_argument, _result, _row) ->
            emit(RcDrop(capturedTemp)("Function")(-1)(true)(false)(None))(state)
        | SemTuple(_elements) as tuple -> emitOwnedValueRelease(emit)(capturedTemp)(tuple)(state)
        | SemNamed(_symbolId, _name, _arguments) as named -> emitOwnedValueRelease(emit)(capturedTemp)(named)(state)
        | _ -> state

let recursive emitClosureCaptureDrops (owned: List((Int, Str, SemanticType))) (environmentTemp: Int) (state: CoreLoweringState) =
    match owned with
        | [] -> state
        | (offset, _text, resolved) :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = capturedTemp } ->
                    allocated
                    |> emit(LoadMemOffset(capturedTemp)(environmentTemp)(offset))
                    |> emitClosureCaptureDrop(capturedTemp)(resolved)
                    |> emitClosureCaptureDrops(rest)(environmentTemp)

let recursive ownedCaptureKey (owned: List((Int, Str, SemanticType))) =
    match owned with
        | [] -> []
        | (offset, text, _resolved) :: rest -> (offset, text) :: ownedCaptureKey(rest)

let closureDropperOrigin (label: Str) (key: Str) =
    IrFunctionOrigin(
        generatedLabel = label,
        originKind = RuntimeManagedClosureDropperOrigin,
        sourceOrigin = None,
        parentGeneratedLabel = None,
        compilerOwner = Some(CompilerFunctionOwner(ownerKind = RuntimeLayoutFunctionOwner, ownerName = key)),
        stableDiscriminator = Some(key),
        generationLocation = None
    )

// Stage 0's `SynthesizeRuntimeManagedClosureDropper`: the `__rc_cdrop_N` function (called with
// the closure's own environment in slot 0 and the environment to release in slot 1) that
// releases every owned capture, synthesized once per capture layout and taking a lambda id like
// any lifted function; its body is built on a scratch frame that borrows the state's label
// counter and the closure's location. A capture's release may synthesize the structural dropper
// of its type: that function and the program state caching its label are kept with the result.
let synthesizeClosureDropper (owned: List((Int, Str, SemanticType))) (state: CoreLoweringState) =
    (let key =
        owned
        |> ownedCaptureKey
        |> captureLayoutText
    in
        match state
        |> stateClosureDropperLabels
        |> lookupClosureDropperLabel(key) with
            | Some(label) -> (state, label)
            | None ->
                let label = "__rc_cdrop_" + Ashes.Text.fromInt(state.nextLambdaId)
                in
                    match freshLocal(withStateClosureDropperLabels((key, label) :: stateClosureDropperLabels(state))(withStateRetiredLocals([])((state with reversedInstructions = [], nextTemp = 0, nextLocal = 0, nextLambdaId = state.nextLambdaId + 1)))) with
                        | FreshLocal { state = ownSlotState } ->
                            match freshLocal(ownSlotState) with
                                | FreshLocal { state = targetSlotState, local = targetSlot } ->
                                    match freshTemp(targetSlotState) with
                                        | FreshTemp { state = environmentState, temp = environmentTemp } ->
                                            match environmentState
                                            |> emit(LoadLocal(environmentTemp)(targetSlot))
                                            |> emitClosureCaptureDrops(owned)(environmentTemp)
                                            |> freshTemp with
                                                | FreshTemp { state = resultState, temp = resultTemp } ->
                                                    match resultState
                                                    |> emit(LoadConstInt(resultTemp)(0))
                                                    |> emit(Return(resultTemp)) with
                                                        | CoreLoweringState { reversedInstructions = instructions, nextTemp = tempCount, nextLocal = localCount, functions = functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId, programState = programState } ->
                                                            ((state with functions = IrFunction(
                                                                label = label,
                                                                instructions = reverse(instructions),
                                                                localCount = localCount,
                                                                tempCount = tempCount,
                                                                hasEnvAndArgParams = true,
                                                                coroutine = None,
                                                                localNames = [],
                                                                localTypes = [],
                                                                origin = key
                                                                |> closureDropperOrigin(label)
                                                                |> Some,
                                                                lifetimesPlaced = false
                                                            ) :: functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId, programState = programState), label))

let classifyClosureCaptureLayout (resolved: SemanticType) (state: CoreLoweringState) =
    (let facts =
        state
        |> coverageEnvironment
        |> classifyHeapCopyPlan(resolved)
    in facts.runtimeRecordAdtSupported || facts.runtimeOwnedChildAdtSupported)

// A cached Boolean query must not allocate copies of the cached child descriptors.
let heapClosureCaptureLayoutOf (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemNamed(_symbolId, name, []) as resolved ->
            match state
            |> stateHeapLayoutFactsByType
            |> Ashes.Collection.Map.getStr(name) with
                | Some(facts) -> facts.runtimeRecordAdtSupported || facts.runtimeOwnedChildAdtSupported
                | None -> classifyClosureCaptureLayout(resolved)(state)
        | resolved -> classifyClosureCaptureLayout(resolved)(state)

// Stage 0's `CanRuntimeNormalizeClosureCapture`: a scalar, a string, bytes or big integer, a list
// over scalars, and a named type that is not a resource and that the record or owned-child layout
// manages.
let canNormalizeClosureCapture (resolved: SemanticType) (state: CoreLoweringState) =
    match resolved with
        | SemString -> true
        | SemBytes -> true
        | SemBigInt -> true
        | SemList(element) ->
            resultSurvivesReset(resolveType(state)(element))(state)
        | SemNamed(_symbolId, _name, _arguments) as named ->
            if isResourceHandle(named)(state)
            then false
            else heapClosureCaptureLayoutOf(named)(state)
        | scalar ->
            match scalarCaptureText(scalar) with
                | Some(_text) -> true
                | None -> false

// Evidence fields are either method closures or nested dictionaries of methods.
let recursive dictionaryCopyPlan semanticType =
    match semanticType with
        | SemFunction(_argument, _result, _row) -> Some(ClosureArgumentCopy)
        | SemTuple(elements) ->
            match dictionaryFieldPlans(elements) with
                | Some(plans) -> Some(TupleArgumentCopy(plans))
                | None -> None
        | _ -> None
and dictionaryFieldPlans elements =
    match elements with
        | [] -> Some([])
        | element :: rest ->
            match (dictionaryCopyPlan(element), dictionaryFieldPlans(rest)) with
                | (Some(first), Some(remaining)) -> Some(first :: remaining)
                | _ -> None

let recursive capturesTraitEvidence captures =
    match captures with
        | [] -> false
        | CoreBinding { name = name } :: rest -> Ashes.Text.startsWith(name)("__trait_") || capturesTraitEvidence(rest)

let isSelfCapture location =
    match location with
        | CoreSelf(_label, _slot) -> true
        | _ -> false

let recursive capturesSelfClosure captures =
    match captures with
        | [] -> false
        | CoreBinding { location = location } :: rest -> isSelfCapture(location) || capturesSelfClosure(rest)

// A copied recursive environment owns its normalized self reference. Its other words remain
// shared unless they carry trait evidence. Copy shared words after the owned slots and leave
// them out of the dropper and the normalizer's owned layout.
let recursive deepCopyCaptureLayout (self: Bool) (evidence: Bool) (captures: List(CoreBinding)) (index: Int) (state: CoreLoweringState) =
    match captures with
        | [] -> Some(([], []))
        | CoreBinding { name = name, location = location, ownedRead = owned, scheme = TypeScheme { body = captureType } } :: rest ->
            let resolved = resolveType(state)(captureType)
            in
                if (self || evidence) && owned && (isResourceHandle(resolved)(state) || heapResourceOf(resolved)(state))
                then None
                else
                    match deepCopyCaptureLayout(self)(evidence)(rest)(index + 1)(state) with
                        | None -> None
                        | Some((normalized, shared)) ->
                            let specialPlan =
                                match resolved with
                                    | SemFunction(_arg, _result, _row) ->
                                        if evidence || isSelfCapture(location)
                                        then Some(ClosureArgumentCopy)
                                        else None
                                    | SemTuple(_fields) ->
                                        if evidence && Ashes.Text.startsWith(name)("__trait_")
                                        then dictionaryCopyPlan(resolved)
                                        else None
                                    | _ -> None
                            in
                                match specialPlan with
                                    | Some(plan) -> Some(((index * 8, Ashes.Trait.Show.show(resolved), plan, resolved) :: normalized, shared))
                                    | None ->
                                        let sharedVariable =
                                            match resolved with
                                                | SemVariable(_id) -> evidence
                                                | _ -> false
                                        in
                                            if self || sharedVariable
                                            then Some((normalized, (index * 8, "word", ScalarArgumentCopy, resolved) :: shared))
                                            else
                                                if canNormalizeClosureCapture(resolved)(state)
                                                then
                                                    match (captureTypeText(resolved), generalCopyPlanOf(resolved)(state)) with
                                                        | (Some(text), Some(plan)) -> Some(((index * 8, text, plan, resolved) :: normalized, shared))
                                                        | _ -> None
                                                else None

let recursive deepCopyOwnedCaptures (layout: List((Int, Str, ArgumentCopyPlan, SemanticType))) =
    match layout with
        | [] -> []
        | (_offset, _text, ScalarArgumentCopy, _resolved) :: rest -> deepCopyOwnedCaptures(rest)
        | (offset, text, _plan, resolved) :: rest -> (offset, text, resolved) :: deepCopyOwnedCaptures(rest)

let recursive deepCopyOffsetsAndTexts (layout: List((Int, Str, ArgumentCopyPlan, SemanticType))) =
    match layout with
        | [] -> []
        | (offset, text, _plan, _resolved) :: rest -> (offset, text) :: deepCopyOffsetsAndTexts(rest)

let recursive emitEnvironmentCaptureCopies (layout: List((Int, Str, ArgumentCopyPlan, SemanticType))) (sourceTemp: Int) (targetTemp: Int) (state: CoreLoweringState) =
    match layout with
        | [] -> state
        | (offset, _text, plan, _resolved) :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = capturedTemp } ->
                    match allocated
                    |> emit(LoadMemOffset(capturedTemp)(sourceTemp)(offset))
                    |> emitGuardedDeepCopy(capturedTemp)(plan) with
                        | (copied, normalizedTemp) ->
                            copied
                            |> emit(StoreMemOffset(targetTemp)(offset)(normalizedTemp))
                            |> emitEnvironmentCaptureCopies(rest)(sourceTemp)(targetTemp)

// Stage 0's `SynthesizeRuntimeManagedClosureNormalizer` for captures the deep copy re-establishes:
// the helper's body is built on a scratch frame that keeps the label counter, reads each capture
// from the source environment in slot 0, copies it, and stores it into the target environment in
// slot 1; it answers the dropper of the owned captures, or zero when every capture is a scalar.
let synthesizeDeepCopyClosureNormalizer (closureLabel: Str) (closureOrigin: IrFunctionOrigin) (layout: List((Int, Str, ArgumentCopyPlan, SemanticType))) (shared: List((Int, Str, ArgumentCopyPlan, SemanticType))) (dropperLabel: Maybe(Str)) (state: CoreLoweringState) =
    match freshLocal(withStateRetiredLocals([])((state with reversedInstructions = [], nextTemp = 0, nextLocal = 0))) with
        | FreshLocal { state = sourceSlotState, local = sourceSlot } ->
            match freshLocal(sourceSlotState) with
                | FreshLocal { state = targetSlotState, local = targetSlot } ->
                    match freshTempRun(2)(targetSlotState) with
                        | FreshTemp { state = loadState, temp = sourceTemp } ->
                            match loadState
                            |> emit(LoadLocal(sourceTemp)(sourceSlot))
                            |> emit(LoadLocal(sourceTemp + 1)(targetSlot))
                            |> emitEnvironmentCaptureCopies(layout)(sourceTemp)(sourceTemp + 1)
                            |> emitEnvironmentCaptureCopies(shared)(sourceTemp)(sourceTemp + 1)
                            |> freshTemp with
                                | FreshTemp { state = resultState, temp = resultTemp } ->
                                    match resultState
                                    |> emit(match dropperLabel with
                                        | Some(label) -> LoadFuncAddr(resultTemp)(label)
                                        | None -> LoadConstInt(resultTemp)(0))
                                    |> emit(Return(resultTemp)) with
                                        | CoreLoweringState { reversedInstructions = instructions, nextTemp = tempCount, nextLocal = localCount, functions = functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId, programState = programState } ->
                                            state with functions = IrFunction(
                                                label = closureLabel + "$env_normalize",
                                                instructions = reverse(instructions),
                                                localCount = localCount,
                                                tempCount = tempCount,
                                                hasEnvAndArgParams = true,
                                                coroutine = None,
                                                localNames = [],
                                                localTypes = [],
                                                origin = layout
                                                |> deepCopyOffsetsAndTexts
                                                |> captureLayoutText
                                                |> closureNormalizerOrigin(closureLabel + "$env_normalize")(closureLabel)(closureOrigin)
                                                |> Some,
                                                lifetimesPlaced = false
                                            ) :: functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId, programState = programState

// Stage 0's `AttachRuntimeManagedClosureNormalizer`: a capturing closure whose captures all
// resolve to types the normalizer re-establishes gets its normalizer (and the dropper of its
// owned captures) right after its own function, as stage 0 synthesizes them when the closure
// is emitted. A closure with a capture still unresolved is recorded for the decision at program
// finalization, when the substitution and the deferred operator defaults are final and only
// scalar captures can still qualify; a capture-free closure needs none.
let recordClosureNormalizer (closureLabel: Str) (captures: List(CoreBinding)) (closureOrigin: IrFunctionOrigin) (state: CoreLoweringState) =
    match captures with
        | [] -> state
        | _ ->
            match deepCopyCaptureLayout(capturesSelfClosure(captures))(capturesTraitEvidence(captures))(captures)(0)(state) with
                | Some(([], _shared)) -> state
                | Some((layout, shared)) ->
                    match match deepCopyOwnedCaptures(layout) with
                        | [] -> (state, None)
                        | owned ->
                            match synthesizeClosureDropper(owned)(state) with
                                | (synthesized, label) -> (synthesized, Some(label)) with
                        | (dropped, dropperLabel) -> synthesizeDeepCopyClosureNormalizer(closureLabel)(closureOrigin)(layout)(shared)(dropperLabel)(dropped)
                | None -> withStatePendingClosureNormalizers((closureLabel, closureOrigin, captureTypes(captures), currentLocation(state)) :: statePendingClosureNormalizers(state))(state)

// Whether the closure's environment normalizer copies or retains a capture: its deep-copy layout
// is decided at the closure and names at least one capture. A layout decided at program
// finalization covers scalar captures only.
let closureNormalizerCopiesCaptures (captures: List(CoreBinding)) (state: CoreLoweringState) =
    match captures with
        | [] -> false
        | _ ->
            match deepCopyCaptureLayout(capturesSelfClosure(captures))(capturesTraitEvidence(captures))(captures)(0)(state) with
                | Some((_copied :: _rest, _shared)) -> true
                | _ -> false

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
                    |> insertAfterLabel(closureLabel)(wordClosureNormalizerFunction(closureLabel)(closureOrigin)(layout)(location))
                    |> insertClosureNormalizers(rest)(defaulted)(state)
                | None -> insertClosureNormalizers(rest)(defaulted)(state)(functions)

let finishClosureResult parameterType bodyType finishedBody closure =
    match closure with
        | (closureState, closureTemp) ->
            let resultType = resolveType(finishedBody)(bodyType)
            in
                success(closureTemp)(SemFunction(parameterType)(resultType)(None))(closureState)

// Stage 0's `ClassifyLambdaClosureCaptures` for a runtime-managed closure, its normalized-parameter
// half: the entry-normalized string parameter a capture moves into the environment has no scope
// owner of its own, so the closure's dropper releases it from its environment word.
let recursive runtimeClosureOwnedCaptures (captures: List(CoreBinding)) (index: Int) (state: CoreLoweringState) =
    match captures with
        | [] -> []
        | CoreBinding { name = name } :: rest ->
            if isNormalizedAlwaysReturnedStringParameterRead(ExprVar(name))(state)
            then (index * 8, Ashes.Trait.Show.show(SemString), SemString) :: runtimeClosureOwnedCaptures(rest)(index + 1)(state)
            else runtimeClosureOwnedCaptures(rest)(index + 1)(state)

// Stage 0's `LowerLambdaCoreMakeClosure`: a runtime-managed closure that owns captures stores the
// address of their dropper in its fourth word, which its release calls on the environment.
let attachRuntimeClosureDropper (runtimeManaged: Bool) (captures: List(CoreBinding)) (closure: (CoreLoweringState, Int)) =
    match closure with
        | (closureState, closureTemp) ->
            if runtimeManaged
            then
                match runtimeClosureOwnedCaptures(captures)(0)(closureState) with
                    | [] -> closure
                    | owned ->
                        match synthesizeClosureDropper(owned)(closureState) with
                            | (synthesized, dropperLabel) ->
                                match freshTemp(synthesized) with
                                    | FreshTemp { state = allocated, temp = dropperTemp } ->
                                        (allocated
                                        |> emit(LoadFuncAddr(dropperTemp)(dropperLabel))
                                        |> emit(StoreMemOffset(closureTemp)(24)(dropperTemp)), closureTemp)
            else closure

// A runtime-managed closure is a reference the creation site holds: newly produced, so the
// aggregate storing it takes it over rather than copying it.
let markRuntimeClosure (runtimeManaged: Bool) (closureTemp: Int) (state: CoreLoweringState) =
    if runtimeManaged
    then markRuntimeTemp(closureTemp)(RuntimeNewlyProduced)(state)
    else state

let emitPrunedClosureIn (runtimeManaged: Bool) label origin captures stackAllocate parameterType bodyType finishedBody allocated =
    match allocated with
        | LoweredCoreValue { state = environmentState, error = Some(error) } -> failure(environmentState)(error)
        | LoweredCoreValue { state = environmentState, temp = environmentTemp, error = None } ->
            match environmentState
            |> emitClosureIn(runtimeManaged)(label)(environmentTemp)(captureCount(captures))(stackAllocate)
            |> attachRuntimeClosureDropper(runtimeManaged)(captures) with
                | (closureState, closureTemp) ->
                    finishClosureResult(parameterType)(bodyType)(finishedBody)((closureState
                    |> markRuntimeClosure(runtimeManaged)(closureTemp)
                    |> recordClosureNormalizer(label)(captures)(origin), closureTemp))

let emitPrunedClosure label origin captures stackAllocate parameterType bodyType finishedBody allocated = emitPrunedClosureIn(false)(label)(origin)(captures)(stackAllocate)(parameterType)(bodyType)(finishedBody)(allocated)

// Every owned child of the dying successor is read, deep-copied and stored into the copy; the
// read temps come back with their types and field indices for the release that follows the
// copies.
let recursive emitBackEdgeChildCopies (sourceTemp: Int) (copyTemp: Int) (tagless: Bool) (children: List((Int, SemanticType))) (reversedRead: List((Int, SemanticType, Int))) (state: CoreLoweringState) =
    match children with
        | [] -> (state, reverse(reversedRead))
        | (index, childType) :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = childTemp } ->
                    match allocated
                    |> unlocatedInstruction(GetAdtField(childTemp)(sourceTemp)(index)(tagless))
                    |> emitChildReferenceOrCopy(childTemp)(childCopyPlanOf(childType)(state)) with
                        | (copied, copiedChild) ->
                            copied
                            |> unlocatedInstruction(SetAdtField(copyTemp)(index)(copiedChild)(tagless))
                            |> emitBackEdgeChildCopies(sourceTemp)(copyTemp)(tagless)(rest)((childTemp, childType, index) :: reversedRead)

let recursive expressionAt (index: Int) (expressions: List(Expr)) =
    match expressions with
        | [] -> None
        | expression :: rest ->
            if index == 0
            then Some(expression)
            else expressionAt(index - 1)(rest)

// The field expressions, in field order, of the constructor application or record literal that
// built a successor cell of the named type; `None` for any other expression.
let constructorFieldExpressionsOf (expression: Expr) (semanticType: SemanticType) (state: CoreLoweringState) =
    match (resolveType(state)(semanticType), constructorApplicationOf(unspanArgument(expression))([])(state)) with
        | (SemNamed(_symbolId, name, _arguments), Some((CoreConstructorLayout { name = constructorName }, arguments))) ->
            match state |> stateConstructorsOfType(name) with
                | CoreConstructorLayout { name = soleConstructor } :: [] ->
                    if soleConstructor == constructorName
                    then Some(arguments)
                    else None
                | _ -> None
        | _ -> None

// A constructor application, record, tuple, list, or cons literal: built in the arena when it is
// a child of a back-edge successor, so the cell carries no reference count.
let isFreshAggregateLiteral (expression: Expr) (state: CoreLoweringState) =
    match unspanArgument(expression) with
        | ExprRecord(_name, _fields, _isMultiline) -> true
        | ExprTuple(_elements) -> true
        | ExprList(_elements, _isMultiline) -> true
        | ExprCons(_head, _tail) -> true
        | other -> isConstructorExpression(other)(state)

// A child released whole is tested first: a successor that is not written as a literal where it
// is copied (a name bound out of a tuple, a join) may still hold an arena cell built this
// iteration, which has no reference count to release.
let emitGuardedSourceChildDrop (childTemp: Int) (childType: SemanticType) (state: CoreLoweringState) =
    if canTestRepresentation(state) == false
    then emitOwnedValueRelease(unlocatedInstruction)(childTemp)(childType)(state)
    else
        match freshTemp(state) with
            | FreshTemp { state = tested, temp = referenceCountedTemp } ->
                match tested
                |> emit(IsReferenceCounted(referenceCountedTemp)(childTemp))
                |> freshLabel("rc_source_child_in_arena") with
                    | FreshLabel { state = labelled, label = arenaLabel } ->
                        labelled
                        |> emit(JumpIfFalse(referenceCountedTemp)(arenaLabel))
                        |> emitOwnedValueRelease(unlocatedInstruction)(childTemp)(childType)
                        |> emit(Label(arenaLabel))

// The temp the construction of `construction`, when this function built that cell, stored into
// field `index` (stage 0's `ConstructionFieldSource`).
let constructionFieldOf (construction: Maybe(Int)) (index: Int) (state: CoreLoweringState) =
    match construction with
        | Some(cellTemp) -> constructionFieldSource(cellTemp)(index)(state.reversedInstructions)
        | None -> None

// The temp the construction of the list or tuple cell `construction` stored at `offsetBytes`
// (stage 0's `ConstructionWordSource`).
let constructionWordOf (construction: Maybe(Int)) (offsetBytes: Int) (state: CoreLoweringState) =
    match construction with
        | Some(cellTemp) -> constructionWordSource(cellTemp)(offsetBytes)(state.reversedInstructions)
        | None -> None

// Whether `temp` was stored into one of `slots` after its definition: the walk runs from the most
// recent instruction back and stops at the instruction defining `temp`.
let recursive storesIntoSlotOf (temp: Int) (slots: List((Int, SemanticType))) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = StoreLocal(slot, source) } :: rest ->
            if source == temp && ownedSlotListed(slot)(slots)
            then true
            else storesIntoSlotOf(temp)(slots)(rest)
        | IrInstruction { instruction = kind } :: rest ->
            if kind
            |> getDefinedTemps
            |> containsInt(temp)
            then false
            else storesIntoSlotOf(temp)(slots)(rest)
and ownedSlotListed (slot: Int) (slots: List((Int, SemanticType))) =
    match slots with
        | [] -> false
        | (candidate, _semanticType) :: rest -> candidate == slot || ownedSlotListed(slot)(rest)

// Whether the constructor argument stored as `stored` went to an owned slot of the function being
// lowered rather than to the arena cell storing it (stage 0's `_ownedSlotConstructorArguments`):
// the slot releases the value, so a release of the cell's children skips that field.
let storedInOwnedSlot (stored: Maybe(Int)) (state: CoreLoweringState) =
    match stored with
        | Some(temp) ->
            match ownedSlotsOfActiveFunction(state) with
                | [] -> false
                | owned -> storesIntoSlotOf(temp)(owned)(state.reversedInstructions)
        | None -> false

// The release of one child the dying successor holds (stage 0's
// `EmitRuntimeManagedTcoSourceChildRelease`): a child the construction handed to an owned slot is
// that slot's to release; a child the construction built as a fresh aggregate literal is an arena
// cell rather than a reference, so only the references the literal itself holds are released,
// recursively; any other child is a reference the construction retained and is released whole,
// unless the retain the construction stored (`stored`) was left without effect, when the cell
// only borrowed the child.
// Stage 0's `IsBorrowedBindingChild`: a child the construction stored as a borrowed read of a
// binding (`State(refs = [text])` with `text` matched out of the list the loop walks) took no
// reference; the binding's owner releases the one it holds.
let isBorrowedBindingChild (expression: Maybe(Expr)) (stored: Maybe(Int)) (state: CoreLoweringState) =
    match (expression, stored) with
        | (Some(child), Some(storedTemp)) ->
            match unspanArgument(child) with
                | ExprVar(_name) -> isBorrowedRead(storedTemp)(state.reversedInstructions)
                | _ -> false
        | _ -> false

let recursive emitSourceChildRelease (childTemp: Int) (childType: SemanticType) (expression: Maybe(Expr)) (stored: Maybe(Int)) (state: CoreLoweringState) =
    if isBorrowedBindingChild(expression)(stored)(state)
    then state
    else
        match expression with
            | Some(literal) ->
                if storedInOwnedSlot(stored)(state)
                then state
                else
                    if isFreshAggregateLiteral(literal)(state)
                    then
                        emitLiteralChildrenRelease(childTemp)(childType)(unspanArgument(literal))(stored)(state)
                    else emitStoredSourceChildRelease(childTemp)(childType)(stored)(state)
            | None -> emitStoredSourceChildRelease(childTemp)(childType)(stored)(state)
and emitStoredSourceChildRelease (childTemp: Int) (childType: SemanticType) (stored: Maybe(Int)) (state: CoreLoweringState) =
    match stored with
        | Some(storedTemp) ->
            if isUnrealizedConstructionRetain(storedTemp)(state.reversedInstructions) || isBareFieldRead(storedTemp)(state.reversedInstructions)
            then state
            else emitGuardedSourceChildDrop(childTemp)(childType)(state)
        | None -> emitGuardedSourceChildDrop(childTemp)(childType)(state)
and emitLiteralChildrenRelease (cellTemp: Int) (semanticType: SemanticType) (literal: Expr) (construction: Maybe(Int)) (state: CoreLoweringState) =
    match (resolveType(state)(semanticType), literal) with
        | (SemNamed(_symbolId, _name, _arguments) as named, _literal) ->
            match constructorFieldExpressionsOf(literal)(named)(state) with
                | Some(arguments) ->
                    match inlineAdtCopyPlanOf(named)(state) with
                        | Some(ConstructorArgumentCopy((_tag, _sizeBytes, tagless, _childPlans))) ->
                            releaseLiteralConstructorChildren(cellTemp)(tagless)(ownedChildrenOfNamed(named)(state))(arguments)(construction)(state)
                        | _plan -> emitOwnedValueRelease(unlocatedInstruction)(cellTemp)(semanticType)(state)
                | None -> releaseVariantLiteralChildren(cellTemp)(named)(literal)(construction)(state)
        | (SemTuple(elements), ExprTuple(expressions)) ->
            if length(elements) == length(expressions)
            then releaseLiteralTupleElements(cellTemp)(0)(elements)(expressions)(construction)(state)
            else emitGuardedSourceChildDrop(cellTemp)(semanticType)(state)
        | (SemList(element), ExprList(expressions, _isMultiline)) -> releaseLiteralListElements(cellTemp)(element)(expressions)(construction)(state)
        | (SemList(element), ExprCons(head, tail)) ->
            match freshTemp(state) with
                | FreshTemp { state = tailState, temp = tailTemp } ->
                    tailState
                    |> releaseLiteralListHead(cellTemp)(element)(head)(construction)
                    |> unlocatedInstruction(LoadMemOffset(tailTemp)(cellTemp)(8))
                    |> (given (headReleased: CoreLoweringState) ->
                        emitSourceChildRelease(tailTemp)(SemList(element))(Some(tail))(constructionWordOf(construction)(8)(headReleased))(headReleased))
        | _ -> emitGuardedSourceChildDrop(cellTemp)(semanticType)(state)
// Stage 0's `TryEmitRuntimeManagedTcoLiteralChildrenRelease` for a constructor of a type with more
// than one (`Some(span)`): the literal names its constructor, so the references the cell holds are
// that constructor's owned fields, released one by one as a single constructor's are.
and releaseVariantLiteralChildren (cellTemp: Int) (named: SemanticType) (literal: Expr) (construction: Maybe(Int)) (state: CoreLoweringState) =
    match constructorApplicationOf(literal)([])(state) with
        | Some((CoreConstructorLayout { name = constructorName, tagless = tagless }, arguments)) ->
            match heapFactsOf(named)(state) with
                | HeapLayoutFacts { children = children } ->
                    releaseLiteralConstructorChildren(cellTemp)(tagless)(ownedConstructorChildren(constructorName)(children))(arguments)(construction)(state)
        | None -> emitGuardedSourceChildDrop(cellTemp)(named)(state)
and releaseLiteralConstructorChildren (cellTemp: Int) (tagless: Bool) (children: List((Int, SemanticType))) (arguments: List(Expr)) (construction: Maybe(Int)) (state: CoreLoweringState) =
    match children with
        | [] -> state
        | (index, childType) :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = childTemp } ->
                    allocated
                    |> unlocatedInstruction(GetAdtField(childTemp)(cellTemp)(index)(tagless))
                    |> (given (read: CoreLoweringState) ->
                        emitSourceChildRelease(childTemp)(childType)(expressionAt(index)(arguments))(constructionFieldOf(construction)(index)(read))(read))
                    |> releaseLiteralConstructorChildren(cellTemp)(tagless)(rest)(arguments)(construction)
and releaseLiteralTupleElements (cellTemp: Int) (index: Int) (elements: List(SemanticType)) (expressions: List(Expr)) (construction: Maybe(Int)) (state: CoreLoweringState) =
    match (elements, expressions) with
        | (element :: restElements, expression :: restExpressions) ->
            if resultSurvivesReset(element)(state)
            then releaseLiteralTupleElements(cellTemp)(index + 1)(restElements)(restExpressions)(construction)(state)
            else
                match freshTemp(state) with
                    | FreshTemp { state = allocated, temp = elementTemp } ->
                        allocated
                        |> unlocatedInstruction(LoadMemOffset(elementTemp)(cellTemp)(8 * index))
                        |> (given (read: CoreLoweringState) ->
                            emitSourceChildRelease(elementTemp)(element)(Some(expression))(constructionWordOf(construction)(8 * index)(read))(read))
                        |> releaseLiteralTupleElements(cellTemp)(index + 1)(restElements)(restExpressions)(construction)
        | _ -> state
and releaseLiteralListHead (cellTemp: Int) (element: SemanticType) (expression: Expr) (construction: Maybe(Int)) (state: CoreLoweringState) =
    if resultSurvivesReset(element)(state)
    then state
    else
        match freshTemp(state) with
            | FreshTemp { state = allocated, temp = headTemp } ->
                allocated
                |> unlocatedInstruction(LoadMemOffset(headTemp)(cellTemp)(0))
                |> (given (read: CoreLoweringState) ->
                    emitSourceChildRelease(headTemp)(element)(Some(expression))(constructionWordOf(construction)(0)(read))(read))
and releaseLiteralListElements (cellTemp: Int) (element: SemanticType) (expressions: List(Expr)) (construction: Maybe(Int)) (state: CoreLoweringState) =
    match expressions with
        | [] -> state
        | expression :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = tailState, temp = tailTemp } ->
                    tailState
                    |> releaseLiteralListHead(cellTemp)(element)(expression)(construction)
                    |> unlocatedInstruction(LoadMemOffset(tailTemp)(cellTemp)(8))
                    |> (given (headReleased: CoreLoweringState) ->
                        releaseLiteralListElements(tailTemp)(element)(rest)(constructionWordOf(construction)(8)(headReleased))(headReleased))

let recursive releaseBackEdgeSourceChildren (sourceTemp: Int) (read: List((Int, SemanticType, Int))) (fieldExpressions: Maybe(List(Expr))) (state: CoreLoweringState) =
    match read with
        | [] -> state
        | (childTemp, childType, index) :: rest ->
            state
            |> emitSourceChildRelease(childTemp)(childType)(match fieldExpressions with
                | Some(expressions) -> expressionAt(index)(expressions)
                | None -> None)(constructionFieldSource(sourceTemp)(index)(state.reversedInstructions))
            |> releaseBackEdgeSourceChildren(sourceTemp)(rest)(fieldExpressions)

// Stage 0's back-edge copy of an arena successor into a runtime-managed ADT slot: the cell
// copies out whole, a record's owned children copy after it, and the dying successor's own
// references to those children are released, since the copy carries the next iteration's own
// (`EmitRuntimeManagedTcoConstructorDeepCopy` with `releaseSourceChildren`); a child built as a
// fresh literal releases only the references it holds.
// Stage 0 reaches the constructor deep copy of a successor cell through two emitters that each
// allocate a result temp they never use once the walk takes over
// (`TcoBackEdgeNormalizeRuntimeManagedArg`, `EmitRuntimeManagedTcoDeepCopy`), so two temps are
// burned ahead of the copy's own.
// A record of the contract's types takes that copy only when the successor is built at the
// self-call itself (stage 0's `SuccessorOwnsItsChildren`); any other successor of such a record is
// borrowed by the copy, which its normalization helper makes; stage 0 reaches that helper behind a
// second representation test of its own, with one temp burned ahead of each test.
let backEdgeAdtCopyPlanOf (semanticType: SemanticType) (expression: Expr) (state: CoreLoweringState) =
    match (tcoAdtCopyPlanOf(semanticType)(state), constructorFieldExpressionsOf(expression)(semanticType)(state)) with
        | (NormalizerArgumentCopy(named), Some(_fields)) ->
            match inlineAdtCopyPlanOf(named)(state) with
                | Some(ConstructorArgumentCopy(constructorPlan)) -> ConstructorArgumentCopy(constructorPlan)
                | _ -> NormalizerArgumentCopy(named)
        | (plan, _fields) -> plan

// The field expressions of `expression` when it applies the constructor named `constructorName`
// (stage 0's `ConstructorFieldExpressions`); `None` for any other expression.
let constructorFieldExpressionsFor (constructorName: Str) (expression: Expr) (state: CoreLoweringState) =
    match constructorApplicationOf(unspanArgument(expression))([])(state) with
        | Some((CoreConstructorLayout { name = applied }, arguments)) ->
            if applied == constructorName
            then Some(arguments)
            else None
        | None -> None

let recursive backEdgeSwitchCasesOf (layouts: List(CoreConstructorLayout)) (state: CoreLoweringState) =
    match layouts with
        | [] -> (state, [])
        | CoreConstructorLayout { tag = tag } :: rest ->
            match freshLabel("rc_normalize_adt")(state) with
                | FreshLabel { state = labelled, label = label } ->
                    match backEdgeSwitchCasesOf(rest)(labelled) with
                        | (casesState, cases) -> (casesState, IrSwitchCase(tag = tag, label = label) :: cases)

// One branch per constructor of stage 0's `EmitRuntimeManagedTcoAdtDeepCopy` with
// `releaseSourceChildren`: the cell copies out, its owned children copy after it, and the dying
// successor's references to them are released, paired with the successor's field expressions only
// in the branch of the constructor it applies.
let recursive emitBackEdgeSwitchBranches (sourceTemp: Int) (resultSlot: Int) (endLabel: Str) (expression: Expr) (children: List(HeapLayoutChild)) (cases: List(IrSwitchCase)) (layouts: List(CoreConstructorLayout)) (state: CoreLoweringState) =
    match (cases, layouts) with
        | (IrSwitchCase { label = label } :: caseRest, (CoreConstructorLayout { name = constructorName, tagless = tagless } as layout) :: layoutRest) ->
            match state
            |> unlocatedInstruction(Label(label))
            |> freshTemp with
                | FreshTemp { state = allocated, temp = copyTemp } ->
                    match allocated
                    |> unlocatedInstruction(CopyOutArena(copyTemp)(sourceTemp)(layoutAllocationSizeBytes(layout))(true)(RcNormalization)(None))
                    |> emitBackEdgeChildCopies(sourceTemp)(copyTemp)(tagless)(ownedConstructorChildren(constructorName)(children))([]) with
                        | (copied, read) ->
                            copied
                            |> releaseBackEdgeSourceChildren(sourceTemp)(read)(constructorFieldExpressionsFor(constructorName)(expression)(copied))
                            |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
                            |> unlocatedInstruction(StoreLocal(resultSlot)(copyTemp))
                            |> unlocatedInstruction(Jump(endLabel))
                            |> emitBackEdgeSwitchBranches(sourceTemp)(resultSlot)(endLabel)(expression)(children)(caseRest)(layoutRest)
        | _ -> state

// Stage 0's back-edge copy of an arena successor of a type with several constructors
// (`EmitRuntimeManagedTcoAdtDeepCopy` with `releaseSourceChildren`, which `SuccessorOwnsItsChildren`
// grants a type that needs no normalizer): the tag selects the constructor branch, each storing its
// copy into a shared slot. Stage 0 burns two temps ahead of the walk, as for one constructor.
let emitTcoBackEdgeSwitchCopy (sourceTemp: Int) (semanticType: SemanticType) (expression: Expr) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemNamed(_symbolId, name, _arguments) as named ->
            match (heapFactsOf(named)(state), stateConstructorsOfType(name)(state), freshTempRun(2)(state)) with
                | (HeapLayoutFacts { children = children }, layouts, FreshTemp { state = burned }) ->
                    match freshLocal(burned) with
                        | FreshLocal { state = slotState, local = resultSlot } ->
                            match freshTemp(slotState) with
                                | FreshTemp { state = tagState, temp = tagTemp } ->
                                    match tagState
                                    |> unlocatedInstruction(GetAdtTag(tagTemp)(sourceTemp))
                                    |> backEdgeSwitchCasesOf(layouts) with
                                        | (casesState, cases) ->
                                            match freshLabel("rc_normalize_adt_end")(casesState) with
                                                | FreshLabel { state = endState, label = endLabel } ->
                                                    match endState
                                                    |> unlocatedInstruction(cases
                                                    |> firstSwitchLabel
                                                    |> SwitchTag(tagTemp)(cases))
                                                    |> emitBackEdgeSwitchBranches(sourceTemp)(resultSlot)(endLabel)(expression)(children)(cases)(layouts)
                                                    |> unlocatedInstruction(Label(endLabel))
                                                    |> freshTemp with
                                                        | FreshTemp { state = resultState, temp = resultTemp } ->
                                                            resultState
                                                            |> unlocatedInstruction(LoadLocal(resultTemp)(resultSlot))
                                                            |> markRuntimeTemp(resultTemp)(RuntimeNewlyProduced)
                                                            |> (given (loaded: CoreLoweringState) -> (loaded, resultTemp))
        | _ -> unlocatedDeepCopy(sourceTemp)(plan)(state)

let emitTcoBackEdgeAdtCopy (sourceTemp: Int) (semanticType: SemanticType) (expression: Expr) (state: CoreLoweringState) =
    match backEdgeAdtCopyPlanOf(semanticType)(expression)(state) with
        | SwitchArgumentCopy(_constructorPlans) as plan -> emitTcoBackEdgeSwitchCopy(sourceTemp)(semanticType)(expression)(plan)(state)
        | ConstructorArgumentCopy((_tag, sizeBytes, tagless, _childPlans)) ->
            match freshTempRun(3)(state) with
                | FreshTemp { state = allocated, temp = firstTemp } ->
                    let copyTemp = firstTemp + 2
                    in
                        match allocated
                        |> unlocatedInstruction(CopyOutArena(copyTemp)(sourceTemp)(sizeBytes)(true)(RcNormalization)(None))
                        |> emitBackEdgeChildCopies(sourceTemp)(copyTemp)(tagless)(ownedChildrenOfNamed(semanticType)(state))([]) with
                            | (copied, read) ->
                                copied
                                |> releaseBackEdgeSourceChildren(sourceTemp)(read)(constructorFieldExpressionsOf(expression)(semanticType)(state))
                                |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
                                |> (given (released: CoreLoweringState) -> (released, copyTemp))
        | NormalizerArgumentCopy(named) ->
            match freshTemp(state) with
                | FreshTemp { state = reserved } ->
                    emitBackEdgeReferenceOrCopy(sourceTemp)(semanticType)(unlocatedDeepCopy(sourceTemp)(NormalizerArgumentCopy(named)))(reserved)
        | plan -> unlocatedDeepCopy(sourceTemp)(plan)(state)

// A record literal successor of a runtime-managed ADT slot the back edge copies inline releases its
// children after the copy, its field cells' parts among them (stage 0's
// `LiteralSuccessorReleasesItsCells`); only one copied by the type's normalizer leaves them to the
// duplicates' release.
let recursive literalSuccessorReleasesItsCells (slot: Int) (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match arguments with
        | [] -> false
        | TcoResetArgument { parameterSlot = parameterSlot, managedAdt = Some((_activeSlot, semanticType, _typeName)), argumentExpression = expression } :: rest ->
            if parameterSlot == slot
            then
                match unspanArgument(expression) with
                    | ExprRecord(_name, _fields, _isMultiline) ->
                        match backEdgeAdtCopyPlanOf(semanticType)(expression)(state) with
                            | ConstructorArgumentCopy(_plan) -> true
                            | _ -> false
                    | _ -> false
            else literalSuccessorReleasesItsCells(slot)(rest)(state)
        | _argument :: rest -> literalSuccessorReleasesItsCells(slot)(rest)(state)

// Stage 0's `EmitLateCopiedHeadTupleDuplicateDrops` for cell heads: once the successors are copied,
// the duplicate an arena cell's head took is released where its parameter is a list on the
// reference-counted heap (the copy holds a reference of its own) and the duplicate became a real
// retain when the loop's pattern owners were placed.
let recursive emitLateCopiedCellHeadDrops (heads: List((Int, Int, SemanticType))) (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match heads with
        | [] -> state
        | (slot, duplicateTemp, headType) :: rest ->
            if (slotHasManagedListArgument(slot)(arguments) || slotHasManagedAdtArgument(slot)(arguments) && !literalSuccessorReleasesItsCells(slot)(arguments)(state)) && retainsAsRuntimeDuplicate(duplicateTemp)(state.reversedInstructions)
            then
                state
                |> emitOwnedValueRelease(unlocatedInstruction)(duplicateTemp)(headType)
                |> emitLateCopiedCellHeadDrops(rest)(arguments)
            else emitLateCopiedCellHeadDrops(rest)(arguments)(state)

// Stage 0's `RegisterGeneralRcOwnedSlot`: the slot just stored holds a value of the contract's
// types the function owns. Inside a loop it takes a flag of its own, set while this iteration's
// value sits in it.
let registerGeneralRcOwnedSlot (slot: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | None ->
            state
            |> withStateGeneralRcOwnedSlots((stateActiveFunctionOrigin(state), slot, semanticType) :: stateGeneralRcOwnedSlots(state))
            |> recordGeneralRcSlotBranches(slot)
        | Some(_frame) ->
            match freshLocal(state) with
                | FreshLocal { state = flagged, local = freshSlot } ->
                    match freshTemp(flagged) with
                        | FreshTemp { state = allocated, temp = oneTemp } ->
                            allocated
                            |> emit(LoadConstInt(oneTemp)(1))
                            |> emit(StoreLocal(freshSlot)(oneTemp))
                            |> (given (stored: CoreLoweringState) -> withStateGeneralRcOwnedSlots((stateActiveFunctionOrigin(stored), slot, semanticType) :: stateGeneralRcOwnedSlots(stored))(stored))
                            |> (given (stored: CoreLoweringState) -> withStateGeneralRcFreshSlots((stateActiveFunctionOrigin(stored), slot, freshSlot) :: stateGeneralRcFreshSlots(stored))(stored))
                            |> recordGeneralRcSlotBranches(slot)

// Stage 0's `SaveGeneralRcOwnedPredecessor`: inside a loop, what the owned slot about to be stored
// still holds moves into a slot of its own, a value an earlier iteration left to a successor (a
// parameter threaded through a call) that no back edge released. Answers that slot, or -1.
let saveGeneralRcOwnedPredecessor (ownedSlot: Int) (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | None -> (state, -1)
        | Some(_frame) ->
            match freshLocal(state) with
                | FreshLocal { state = slotted, local = predecessorSlot } ->
                    match freshTemp(slotted) with
                        | FreshTemp { state = allocated, temp = previousTemp } ->
                            (allocated
                            |> emit(LoadLocal(previousTemp)(ownedSlot))
                            |> emit(StoreLocal(predecessorSlot)(previousTemp)), predecessorSlot)

// Stage 0's `RegisterGeneralRcOwnedPredecessor`: released at the function's exit like any owned
// slot, and at a back edge by its own rule. It shares the binders of the owned slot it succeeds.
let registerGeneralRcOwnedPredecessor (predecessorSlot: Int) (ownedSlot: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    if predecessorSlot < 0
    then state
    else
        state
        |> withStateGeneralRcOwnedSlots((stateActiveFunctionOrigin(state), predecessorSlot, semanticType) :: stateGeneralRcOwnedSlots(state))
        |> withStateGeneralRcPredecessorSlots((stateActiveFunctionOrigin(state), predecessorSlot, semanticType) :: stateGeneralRcPredecessorSlots(state))
        |> withStateGeneralRcPredecessorOwners((stateActiveFunctionOrigin(state), predecessorSlot, ownedSlot) :: stateGeneralRcPredecessorOwners(state))
        |> recordGeneralRcSlotBranches(predecessorSlot)

let recursive predecessorOwnerOf (origin: Maybe(IrFunctionOrigin)) (predecessorSlot: Int) (owners: List((Maybe(IrFunctionOrigin), Int, Int))) =
    match owners with
        | [] -> -1
        | (owner, candidate, ownedSlot) :: rest ->
            if candidate == predecessorSlot && owner == origin
            then ownedSlot
            else predecessorOwnerOf(origin)(predecessorSlot)(rest)

let recursive slotBindersOf (origin: Maybe(IrFunctionOrigin)) (slot: Int) (binders: List((Maybe(IrFunctionOrigin), Int, List(Str)))) (names: List(Str)) =
    match binders with
        | [] -> names
        | (owner, candidate, bound) :: rest ->
            if candidate == slot && owner == origin
            then
                names
                |> append(bound)
                |> slotBindersOf(origin)(slot)(rest)
            else slotBindersOf(origin)(slot)(rest)(names)

// The release of one owned value by its type, spliced in from the dropper synthesis.
let emitOwnedValueChildDrop (valueTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { nextTemp = nextTemp, nextLocal = nextLocal, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
            spliceInlineRelease(synthesizeChildDrop(valueTemp)(resolveType(state)(semanticType))(dropperTypesOf(state))(cache)(nextTemp)(nextLocal)(lambdaId)(labelId))(state)

// Stage 0's `EmitGeneralRcOwnedSlotDrops` for one slot: a slot that holds a reference-counted
// value releases it and is cleared; an empty slot, or one whose value is still in the arena, is
// left as it is.
let emitGeneralRcOwnedSlotDrop (slot: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = valueState, temp = valueTemp } ->
            match valueState
            |> emit(LoadLocal(valueTemp)(slot))
            |> freshTemp with
                | FreshTemp { state = zeroState, temp = zeroTemp } ->
                    match zeroState
                    |> emit(LoadConstInt(zeroTemp)(0))
                    |> freshTemp with
                        | FreshTemp { state = presentState, temp = presentTemp } ->
                            match presentState
                            |> emit(CmpIntNe(presentTemp)(valueTemp)(zeroTemp))
                            |> freshLabel("general_rc_owned_absent") with
                                | FreshLabel { state = labelled, label = skipLabel } ->
                                    match labelled
                                    |> emit(JumpIfFalse(presentTemp)(skipLabel))
                                    |> freshTemp with
                                        | FreshTemp { state = testedState, temp = referenceCountedTemp } ->
                                            testedState
                                            |> emit(IsReferenceCounted(referenceCountedTemp)(valueTemp))
                                            |> emit(JumpIfFalse(referenceCountedTemp)(skipLabel))
                                            |> emitOwnedValueChildDrop(valueTemp)(semanticType)
                                            |> emit(StoreLocal(slot)(zeroTemp))
                                            |> emit(Label(skipLabel))

let recursive emitGeneralRcOwnedSlotDropsOf (origin: Maybe(IrFunctionOrigin)) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (state: CoreLoweringState) =
    match slots with
        | [] -> state
        | (owner, slot, semanticType) :: rest ->
            if owner == origin
            then
                state
                |> emitGeneralRcOwnedSlotDrop(slot)(semanticType)
                |> emitGeneralRcOwnedSlotDropsOf(origin)(rest)
            else emitGeneralRcOwnedSlotDropsOf(origin)(rest)(state)

// The owned slots of the function being finished, released in the order they were registered.
let emitGeneralRcOwnedSlotDrops (state: CoreLoweringState) =
    emitGeneralRcOwnedSlotDropsOf(stateActiveFunctionOrigin(state))(state
    |> stateGeneralRcOwnedSlots
    |> reverse)(state)

let backEdgeCopyBorrows (semanticType: SemanticType) (expression: Expr) (state: CoreLoweringState) =
    match backEdgeAdtCopyPlanOf(semanticType)(expression)(state) with
        | NormalizerArgumentCopy(_named) -> true
        | _plan -> false

// Stage 0's `BackEdgeCopyBorrowsLiteralElements`: a list literal successor whose arena cells the back
// edge copies borrows its elements' children like a copied record, so the retains an arena element
// was built with for loop-parameter reads are withdrawn.
let withdrawCopiedLiteralRetains (expression: Expr) (temp: Int) (parameters: List((Int, Int))) (state: CoreLoweringState) =
    match unspanArgument(expression) with
        | ExprList(_first :: _rest, _isMultiline) ->
            state with reversedInstructions = withdrawLoopParameterRetainsOfCopiedCells(stateTransientFlagClears(state))(temp)(parameters)(state.reversedInstructions)
        | _ -> state

// Stage 0's `BackEdgeCopyBorrowsSuccessorChildren`: an arena successor of a runtime-managed ADT
// slot whose back-edge copy borrows its children (its normalization helper makes it) rather than
// releasing them from the dying cell. The retains its construction made of loop-parameter reads
// are withdrawn, since the copy takes its own references before the old parameters are released.
let recursive withdrawBorrowedSuccessorRetains (arguments: List(TcoResetArgument)) (parameters: List((Int, Int))) (state: CoreLoweringState) =
    match arguments with
        | [] -> state
        | TcoResetArgument { managedAdt = Some((_activeSlot, semanticType, _typeName)), shape = shape, parameterSlot = slot, argumentTemp = temp, argumentRuntime = false, argumentExpression = expression, passesThrough = passesThrough } :: rest ->
            if shape != TcoPassThroughShape && !passesThrough && backEdgeCopyBorrows(semanticType)(expression)(state)
            then
                withdrawBorrowedSuccessorRetains(rest)(parameters)((state with reversedInstructions = withdrawLoopParameterRetainsOfCopiedCells(stateTransientFlagClears(state))(temp)(parameters)(state.reversedInstructions)))
            else
                state
                |> withdrawCopiedLiteralRetains(expression)(temp)(parameters)
                |> withdrawBorrowedSuccessorRetains(rest)(parameters)
        | TcoResetArgument { managedList = Some(_list), argumentTemp = temp, argumentRuntime = false, argumentExpression = expression } :: rest ->
            state
            |> withdrawCopiedLiteralRetains(expression)(temp)(parameters)
            |> withdrawBorrowedSuccessorRetains(rest)(parameters)
        | _argument :: rest -> withdrawBorrowedSuccessorRetains(rest)(parameters)(state)

// Each loop parameter's slot beside its active flag at a back edge, -1 for one without (stage 0's
// `BackEdgeLoopParameters`).
let recursive resetArgumentParameters (arguments: List(TcoResetArgument)) =
    match arguments with
        | [] -> []
        | TcoResetArgument { parameterSlot = slot, managedList = managedList, managedAdt = managedAdt, managedStr = managedStr } :: rest ->
            (slot, match (managedList, managedAdt, managedStr) with
                | (Some((activeSlot, _elementType)), _adt, _str) -> activeSlot
                | (None, Some((activeSlot, _semanticType, _typeName)), _str) -> activeSlot
                | (None, None, Some(activeSlot)) -> activeSlot
                | _ -> -1) :: resetArgumentParameters(rest)

// Stage 0's `CallResultMayContainArgumentType`: whether a value of the argument's type could be
// reachable inside the result. An unresolved variable, a rigid type parameter, and a function value
// (which can have captured anything) all answer yes, and a yes only ever keeps a handed-over reference
// where it is, so erring this way leaks at worst. A named instantiation is expanded once per walk;
// its type arguments are walked every time regardless. Non-regular recursive expansion at changing
// arguments conservatively answers yes, while different sibling instantiations are searched. A named
// type's fields are read at its instantiation (`instantiated`), so a field of its own type parameter
// is the argument in its place; the loop placement's sibling question reads them as declared.
let typeMayContainArgumentType (instantiated: Bool) (resultType: SemanticType) (argumentType: SemanticType) (expanded: List(Str)) (state: CoreLoweringState) =
    mayContainArgumentType(resolveType(state))(given (semanticType) ->
        match semanticType with
            | SemNamed(_id, name, arguments) -> namedTypeFieldTypesAs(instantiated)(name)(arguments)(state)
            | _ -> [])(expanded)(resultType)(argumentType)

let callResultMayContainArgumentType (resultType: SemanticType) (argumentType: SemanticType) (expanded: List(Str)) (state: CoreLoweringState) = typeMayContainArgumentType(true)(resultType)(argumentType)(expanded)(state)

let callResultMayContainAnyArgumentType (resultType: SemanticType) (argumentTypes: List(SemanticType)) (state: CoreLoweringState) =
    mayContainAnyArgumentType(resolveType(state))(given (semanticType) ->
        match semanticType with
            | SemNamed(_id, name, arguments) -> namedTypeFieldTypesAs(true)(name)(arguments)(state)
            | _ -> [])([])(resultType)(argumentTypes)

// Stage 0's `CollectHeapComponentTypes`: every heap type a value of the type is built from, itself
// included, beside the type names already expanded, and whether all of them are known: false when
// some part may be a value of any type.
let recursive collectHeapComponentTypes (semanticType: SemanticType) (state: CoreLoweringState) (collected: (List(SemanticType), List(Str))) =
    match (resolveType(state)(semanticType), collected) with
        | (SemString, (parts, expanded)) -> (true, (SemString :: parts, expanded))
        | (SemBytes, (parts, expanded)) -> (true, (SemBytes :: parts, expanded))
        | (SemBigInt, (parts, expanded)) -> (true, (SemBigInt :: parts, expanded))
        | (SemList(element), (parts, expanded)) -> collectHeapComponentTypes(element)(state)((SemList(element) :: parts, expanded))
        | (SemTuple(elements), (parts, expanded)) -> collectAllHeapComponentTypes(elements)(state)((SemTuple(elements) :: parts, expanded))
        | (SemNamed(symbolId, name, arguments), (parts, expanded)) ->
            if containsName(name)(expanded)
            then (true, (SemNamed(symbolId)(name)(arguments) :: parts, expanded))
            else
                collectAllHeapComponentTypes(namedTypeFieldTypesAs(true)(name)(arguments)(state))(state)((SemNamed(symbolId)(name)(arguments) :: parts, name :: expanded))
        | (SemVariable(_id), _collected) -> (false, collected)
        | (SemParameter(_id, _name), _collected) -> (false, collected)
        | (SemFunction(_parameter, _result, _row), _collected) -> (false, collected)
        | (SemCapability(_name, _arguments), _collected) -> (false, collected)
        | (SemRow(_capabilities, _tail), _collected) -> (false, collected)
        | (SemPointer(_pointee), _collected) -> (false, collected)
        | _scalar -> (true, collected)
and collectAllHeapComponentTypes (types: List(SemanticType)) (state: CoreLoweringState) (collected: (List(SemanticType), List(Str))) =
    match types with
        | [] -> (true, collected)
        | semanticType :: rest ->
            match collectHeapComponentTypes(semanticType)(state)(collected) with
                | (true, next) -> collectAllHeapComponentTypes(rest)(state)(next)
                | (false, next) -> (false, next)

let anyPartMayBeInResult (resultType: SemanticType) (parts: List(SemanticType)) (state: CoreLoweringState) = callResultMayContainAnyArgumentType(resultType)(parts)(state)

// Stage 0's `ResultTypeMayHoldPartOf`: whether a value of the result's type can hold an owned value
// of the owned type or one of its heap parts. An owned type with a part of unresolved type or a
// function inside may share a value with any result.
let resultTypeMayHoldPartOf (resultType: SemanticType) (ownedType: SemanticType) (state: CoreLoweringState) =
    match collectHeapComponentTypes(ownedType)(state)(([], [])) with
        | (false, _collected) -> true
        | (true, (parts, _expanded)) -> anyPartMayBeInResult(resultType)(parts)(state)

let recursive ownedSlotsOutside (reached: List(Int)) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) =
    match slots with
        | [] -> []
        | ((_owner, slot, _semanticType) as owned) :: rest ->
            if containsInt(slot)(reached)
            then ownedSlotsOutside(reached)(rest)
            else owned :: ownedSlotsOutside(reached)(rest)

// Stage 0's `AddPredecessorsOfReachedSlots`: a predecessor slot holds what its owned slot held in an
// earlier iteration, which flowed on through a loop parameter the reach walk does not follow back,
// so a result reaching the owned slot reaches its predecessor too.
let slotOrOriginReached (owner: Maybe(IrFunctionOrigin)) (slot: Int) (reached: List(Int)) (state: CoreLoweringState) =
    containsInt(slot)(reached) || (match state
    |> stateGeneralRcPredecessorOwners
    |> predecessorOwnerOf(owner)(slot) with
        | -1 -> false
        | origin -> containsInt(origin)(reached))

// The owned slots a result of the given type cannot point into: none of the reached slots, and none
// whose type no part of occurs in the result's type.
let recursive ownedSlotsOutsideResult (resultType: SemanticType) (reached: List(Int)) (slots: List((Maybe(IrFunctionOrigin), Int, SemanticType))) (state: CoreLoweringState) =
    match slots with
        | [] -> []
        | ((owner, slot, semanticType) as owned) :: rest ->
            if slotOrOriginReached(owner)(slot)(reached)(state) && resultTypeMayHoldPartOf(resultType)(semanticType)(state)
            then ownedSlotsOutsideResult(resultType)(reached)(rest)(state)
            else owned :: ownedSlotsOutsideResult(resultType)(reached)(rest)(state)

let recursive typeAtIndex (index: Int) (types: List(SemanticType)) =
    match types with
        | [] -> None
        | head :: rest ->
            if index == 0
            then Some(head)
            else typeAtIndex(index - 1)(rest)

// The type of field `index` of the constructor tagged `tag` of the named type, at its instantiation.
let recursive constructorFieldTypeAt (typeName: Str) (arguments: List(SemanticType)) (tag: Int) (index: Int) (layouts: List(CoreConstructorLayout)) (state: CoreLoweringState) =
    match layouts with
        | [] -> None
        | (CoreConstructorLayout { tag = candidateTag } as layout) :: rest ->
            if candidateTag == tag && constructorResultNameIs(typeName)(layout)
            then
                match layoutFieldTypes(layout)(state) with
                    | (fields, SemNamed(_symbolId, _name, variables)) ->
                        match typeAtIndex(index)(fields) with
                            | Some(fieldType) ->
                                fieldType
                                |> replaceTypeVariables(typeVariablePairs(variables)(arguments))
                                |> Some
                            | None -> None
                    | (fields, _resultType) -> typeAtIndex(index)(fields)
            else constructorFieldTypeAt(typeName)(arguments)(tag)(index)(rest)(state)

// The type of the field a path of constructor tags and field indices reaches from `semanticType`.
let recursive fieldTypeAlong (path: List((Int, Int))) (semanticType: SemanticType) (state: CoreLoweringState) =
    match (path, resolveType(state)(semanticType)) with
        | ([], _) -> Some(semanticType)
        | ((tag, index) :: rest, SemNamed(_symbolId, name, arguments)) ->
            match constructorFieldTypeAt(name)(arguments)(tag)(index)(stateConstructorsOfType(name)(state))(state) with
                | Some(fieldType) -> fieldTypeAlong(rest)(fieldType)(state)
                | None -> None
        | _ -> None

// Stage 0's release of `RetainsHeldPastTheCopy` after an arena successor's back-edge copy: each
// field retain the arena cell held for its head, released now that the copy holds its own.
let recursive emitHeldRetainReleases (retains: List((Int, List((Int, Int))))) (semanticType: SemanticType) (state: CoreLoweringState) =
    match retains with
        | [] -> state
        | (temp, path) :: rest ->
            match fieldTypeAlong(path)(semanticType)(state) with
                | Some(fieldType) ->
                    state
                    |> emitOwnedValueRelease(unlocatedInstruction)(temp)(fieldType)
                    |> emitHeldRetainReleases(rest)(semanticType)
                | None -> emitHeldRetainReleases(rest)(semanticType)(state)

let backEdgeHeldRetains (temp: Int) (semanticType: SemanticType) (expression: Expr) (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | Some(frame) ->
            if backEdgeCopyBorrows(semanticType)(expression)(state)
            then retainsHeldPastTheCopy(temp)(frame.parameterSlots)(state.reversedInstructions)
            else []
        | None -> []

// Stage 0's `TcoBackEdgeNormalizeAndReleaseRuntimeManagedArgs` for the list slots: the
// pattern-bound tail of a consumed list is retained for the successor (null-tolerant, the tail
// may be the empty list), a fresh runtime-managed cons cell already owns its reference, and a
// pass-through keeps the parameter's own value. The successors to store back into their parameter
// slots after the reset come back with their active flags.
// Stage 0's `TcoBackEdgeNormalizeRuntimeManagedArgByCopy` for a list successor: a list whose heads
// have no spine copy is deep-copied past the result temp stage 0 reserves before it, which the walk
// leaves unused.
let backEdgeListCopy (sourceTemp: Int) (plan: ArgumentCopyPlan) (state: CoreLoweringState) =
    match plan with
        | ListDeepArgumentCopy(_elementPlan) ->
            match freshTemp(state) with
                | FreshTemp { state = reserved } -> unlocatedDeepCopy(sourceTemp)(plan)(reserved)
        | _ -> unlocatedDeepCopy(sourceTemp)(plan)(state)

let recursive normalizeRuntimeManagedBackEdgeArguments (arguments: List(TcoResetArgument)) (reversedStores: List((Int, Int, Int))) (state: CoreLoweringState) =
    match arguments with
        | [] -> (state, reverse(reversedStores))
        | TcoResetArgument { managedList = Some((activeSlot, _elementType)), shape = TcoConsumedTailShape, parameterSlot = slot, argumentTemp = temp, argumentRuntime = isRuntime } :: rest ->
            if isRuntime
            then normalizeRuntimeManagedBackEdgeArguments(rest)((slot, activeSlot, temp) :: reversedStores)(state)
            else
                match freshTemp(state) with
                    | FreshTemp { state = allocated, temp = duplicate } ->
                        allocated
                        |> unlocatedInstruction(RcDup(duplicate)(temp)(true)(true))
                        |> markRuntimeTemp(duplicate)(RuntimeNewlyProduced)
                        |> normalizeRuntimeManagedBackEdgeArguments(rest)((slot, activeSlot, duplicate) :: reversedStores)
        // A grown accumulator's successor is the cons cell the argument built: a cell allocated on
        // the reference-counted heap already owns its reference. A list of the contract's elements
        // builds its cell in the arena, and the back edge normalizes it whatever it is: a
        // reference-counted successor is retained, an arena one copied cell by cell with its heads
        // normalized and the reference-counted tail it reaches shared.
        | TcoResetArgument { managedList = Some((activeSlot, elementType)), shape = TcoGrownConsShape, parameterSlot = slot, argumentTemp = temp, argumentType = argumentType, argumentRuntime = isRuntime } :: rest ->
            if isRuntime || !grownAccumulatorNormalizedAtBackEdge(elementType)(state)
            then normalizeRuntimeManagedBackEdgeArguments(rest)((slot, activeSlot, temp) :: reversedStores)(state)
            else
                match generalCopyPlanOf(argumentType)(state) with
                    | None -> normalizeRuntimeManagedBackEdgeArguments(rest)((slot, activeSlot, temp) :: reversedStores)(state)
                    | Some(plan) ->
                        match emitGuardedDeepCopy(temp)(plan)((state with currentSpan = None)) with
                            | (copied, copyTemp) ->
                                (copied with currentSpan = state.currentSpan)
                                |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
                                |> normalizeRuntimeManagedBackEdgeArguments(rest)((slot, activeSlot, copyTemp) :: reversedStores)
        // A walked-or-refilled list's successor is normalized as a choice accumulator's is.
        | (TcoResetArgument { managedList = Some(_managed), shape = TcoWalkedOrRefilledShape } as argument) :: rest -> normalizeRuntimeManagedBackEdgeArguments((argument with shape = TcoChoiceAccumulatorShape) :: rest)(reversedStores)(state)
        | (TcoResetArgument { managedList = Some(_managed), shape = TcoRefilledFreshListShape } as argument) :: rest -> normalizeRuntimeManagedBackEdgeArguments((argument with shape = TcoChoiceAccumulatorShape) :: rest)(reversedStores)(state)
        | (TcoResetArgument { managedList = Some(_managed), shape = TcoAccumulatorRebuildShape } as argument) :: rest -> normalizeRuntimeManagedBackEdgeArguments((argument with shape = TcoChoiceAccumulatorShape) :: rest)(reversedStores)(state)
        // A choice accumulator's successor is whatever the chosen arm handed over: an owned
        // reference-counted list is stored as it is, the parameter's own value is retained, since
        // the predecessor release still runs, and anything else is taken by reference when it
        // turns out reference-counted and copied otherwise.
        | TcoResetArgument { managedList = Some((activeSlot, elementType)), shape = TcoChoiceAccumulatorShape, parameterSlot = slot, argumentTemp = temp, argumentType = argumentType, argumentRuntime = isRuntime, argumentExpression = expression, passesThrough = passesThrough } :: rest ->
            if isRuntime
            then normalizeRuntimeManagedBackEdgeArguments(rest)((slot, activeSlot, temp) :: reversedStores)(state)
            else
                if passesThrough
                then
                    match freshTemp(state) with
                        | FreshTemp { state = allocated, temp = duplicate } ->
                            allocated
                            |> unlocatedInstruction(RcDup(duplicate)(temp)(true)(true))
                            |> markRuntimeTemp(duplicate)(RuntimeNewlyProduced)
                            |> normalizeRuntimeManagedBackEdgeArguments(rest)((slot, activeSlot, duplicate) :: reversedStores)
                else
                    // A list of the contract's values has only the general plan, which copies its
                    // elements through their normalizer; the plan is the one the slot was placed by.
                    match managedListCopyPlanOf(argumentType)(elementType)(state) with
                        | None -> normalizeRuntimeManagedBackEdgeArguments(rest)((slot, activeSlot, temp) :: reversedStores)(state)
                        | Some(plan) ->
                            match emitBackEdgeReferenceOrCopy(temp)(argumentType)(backEdgeListCopy(temp)(plan))(state) with
                                | (copied, copyTemp) ->
                                    copied
                                    |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
                                    |> normalizeRuntimeManagedBackEdgeArguments(rest)((slot, activeSlot, copyTemp) :: reversedStores)
        | TcoResetArgument { managedAdt = Some((activeSlot, semanticType, _typeName)), shape = shape, parameterSlot = slot, argumentTemp = temp, argumentRuntime = isRuntime, argumentExpression = expression, passesThrough = passesThrough } :: rest ->
            if shape == TcoPassThroughShape
            then normalizeRuntimeManagedBackEdgeArguments(rest)(reversedStores)(state)
            else
                if isRuntime
                then normalizeRuntimeManagedBackEdgeArguments(rest)((slot, activeSlot, temp) :: reversedStores)(state)
                else
                    // A back edge passing the parameter's own value on, when another edge
                    // rebuilds it: the read is the reference-counted value the parameter holds
                    // (stage 0's `TcoBackEdgeRetainRuntimeManagedArg`), retained for the next
                    // iteration since the predecessor release still runs. A list may be the empty
                    // list, so its retain is guarded.
                    if passesThrough
                    then
                        match freshTemp(state) with
                            | FreshTemp { state = allocated, temp = duplicate } ->
                                allocated
                                |> unlocatedInstruction(semanticType
                                |> resolveType(state)
                                |> mayBeEmptyList
                                |> RcDup(duplicate)(temp)(true))
                                |> markRuntimeTemp(duplicate)(RuntimeNewlyProduced)
                                |> normalizeRuntimeManagedBackEdgeArguments(rest)((slot, activeSlot, duplicate) :: reversedStores)
                    else
                        let heldRetains = backEdgeHeldRetains(temp)(semanticType)(expression)(state)
                        in
                            match emitBackEdgeReferenceOrCopy(temp)(semanticType)(emitTcoBackEdgeAdtCopy(temp)(semanticType)(expression))(state) with
                                | (copied, copyTemp) ->
                                    copied
                                    |> emitHeldRetainReleases(heldRetains)(semanticType)
                                    |> normalizeRuntimeManagedBackEdgeArguments(rest)((slot, activeSlot, copyTemp) :: reversedStores)
        | TcoResetArgument { managedStr = Some(activeSlot), parameterSlot = slot, argumentTemp = temp, argumentRuntime = isRuntime, argumentExpression = expression, passesThrough = passesThrough } :: rest ->
            if passesThrough
            then normalizeRuntimeManagedBackEdgeArguments(rest)(reversedStores)(state)
            else
                if isRuntime
                then normalizeRuntimeManagedBackEdgeArguments(rest)((slot, activeSlot, temp) :: reversedStores)(state)
                else
                    match emitBackEdgeReferenceOrCopy(temp)(SemString)(unlocatedDeepCopy(temp)(LeafArgumentCopy(-1)))(state) with
                        | (copied, copyTemp) ->
                            copied
                            |> markRuntimeTemp(copyTemp)(RuntimeNewlyProduced)
                            |> normalizeRuntimeManagedBackEdgeArguments(rest)((slot, activeSlot, copyTemp) :: reversedStores)
        | _argument :: rest -> normalizeRuntimeManagedBackEdgeArguments(rest)(reversedStores)(state)

// Whether `temp` is the result of an in-place affine append: the append consumed the
// accumulator's own reference (extended in place, or released by the fallback that copied it),
// so the back edge has no predecessor left to release (stage 0's `IsRuntimeManagedConcatStrTipResult`).
let recursive isAffineAppendResult (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = ConcatStrTip(target, _left, _right, _start, _end, _runtimeManaged) } :: rest -> target == temp || isAffineAppendResult(temp)(rest)
        | _instruction :: rest -> isAffineAppendResult(temp)(rest)

// A runtime-managed `Str` slot's predecessor is released under the active flag unless the
// argument is the parameter's own untouched value or the in-place append that consumed it.
let strPredecessorIsReleased (argument: TcoResetArgument) (state: CoreLoweringState) = !argument.passesThrough && !isAffineAppendResult(argument.argumentTemp)(state.reversedInstructions)

// The old root of a consumed list is released once its tail is retained for the successor
// (stage 0's `TcoBackEdgeDropRuntimeManagedArg` under the active flag): a unique cell frees
// its head and continues into the tail, which the retain keeps alive.
// A self-call that starts a grown accumulator afresh (`first :: []`) moves nothing of the
// predecessor into its cells, stage 0's `MovesPredecessorIntoTail` answering no.
let recursive startsListAfresh (expression: Expr) =
    match unspanArgument(expression) with
        | ExprList(_elements, _isMultiline) -> true
        | ExprCons(_head, tail) -> startsListAfresh(tail)
        | _ -> false

let recursive dropRuntimeManagedBackEdgePredecessors (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match arguments with
        | [] -> state
        // A grown accumulator started afresh releases its predecessor, which no cell took over.
        | TcoResetArgument { managedList = Some((activeSlot, elementType)), shape = TcoGrownConsShape, oldTemp = oldTemp, argumentRuntime = true, argumentExpression = expression } :: rest ->
            if startsListAfresh(expression)
            then
                state
                |> emitGuardedListRelease(unlocatedInstruction)("rc_tco_drop_inactive")(activeSlot)(oldTemp)(elementType)
                |> dropRuntimeManagedBackEdgePredecessors(rest)
            else dropRuntimeManagedBackEdgePredecessors(rest)(state)
        | TcoResetArgument { managedList = Some((activeSlot, elementType)), shape = TcoConsumedTailShape, oldTemp = oldTemp } :: rest ->
            state
            |> emitGuardedListRelease(unlocatedInstruction)("rc_tco_drop_inactive")(activeSlot)(oldTemp)(elementType)
            |> dropRuntimeManagedBackEdgePredecessors(rest)
        // The successor of a list of the contract's elements was normalized, so it holds its own
        // reference to the tail it shares, and the predecessor is released like a consumed list's.
        | TcoResetArgument { managedList = Some((activeSlot, elementType)), shape = TcoGrownConsShape, oldTemp = oldTemp, argumentRuntime = false } :: rest ->
            if grownAccumulatorNormalizedAtBackEdge(elementType)(state)
            then
                state
                |> emitGuardedListRelease(unlocatedInstruction)("rc_tco_drop_inactive")(activeSlot)(oldTemp)(elementType)
                |> dropRuntimeManagedBackEdgePredecessors(rest)
            else dropRuntimeManagedBackEdgePredecessors(rest)(state)
        | TcoResetArgument { managedList = Some((activeSlot, elementType)), shape = TcoChoiceAccumulatorShape, oldTemp = oldTemp } :: rest ->
            state
            |> emitGuardedListRelease(unlocatedInstruction)("rc_tco_drop_inactive")(activeSlot)(oldTemp)(elementType)
            |> dropRuntimeManagedBackEdgePredecessors(rest)
        | TcoResetArgument { managedList = Some((activeSlot, elementType)), shape = TcoWalkedOrRefilledShape, oldTemp = oldTemp } :: rest ->
            state
            |> emitGuardedListRelease(unlocatedInstruction)("rc_tco_drop_inactive")(activeSlot)(oldTemp)(elementType)
            |> dropRuntimeManagedBackEdgePredecessors(rest)
        | TcoResetArgument { managedList = Some((activeSlot, elementType)), shape = TcoRefilledFreshListShape, oldTemp = oldTemp } :: rest ->
            state
            |> emitGuardedListRelease(unlocatedInstruction)("rc_tco_drop_inactive")(activeSlot)(oldTemp)(elementType)
            |> dropRuntimeManagedBackEdgePredecessors(rest)
        | TcoResetArgument { managedList = Some((activeSlot, elementType)), shape = TcoAccumulatorRebuildShape, oldTemp = oldTemp } :: rest ->
            state
            |> emitGuardedListRelease(unlocatedInstruction)("rc_tco_drop_inactive")(activeSlot)(oldTemp)(elementType)
            |> dropRuntimeManagedBackEdgePredecessors(rest)
        | TcoResetArgument { managedAdt = Some((activeSlot, semanticType, _typeName)), shape = shape, oldTemp = oldTemp } :: rest ->
            if shape == TcoPassThroughShape
            then dropRuntimeManagedBackEdgePredecessors(rest)(state)
            else
                state
                |> emitGuardedAdtRelease(unlocatedInstruction)("rc_tco_drop_inactive")(activeSlot)(oldTemp)(semanticType)
                |> dropRuntimeManagedBackEdgePredecessors(rest)
        | (TcoResetArgument { managedStr = Some(activeSlot), oldTemp = oldTemp } as argument) :: rest ->
            if strPredecessorIsReleased(argument)(state)
            then
                state
                |> emitGuardedAdtRelease(unlocatedInstruction)("rc_tco_drop_inactive")(activeSlot)(oldTemp)(SemString)
                |> dropRuntimeManagedBackEdgePredecessors(rest)
            else dropRuntimeManagedBackEdgePredecessors(rest)(state)
        | _argument :: rest -> dropRuntimeManagedBackEdgePredecessors(rest)(state)

// The `Str` slots' predecessor releases when the frame cannot take the runtime-managed reset
// (a sibling keeps the iteration's arena allocations alive): the successors already own their
// references, so only the predecessors go, as the runtime-managed reset would release them.
let recursive dropStrBackEdgePredecessors (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match arguments with
        | [] -> state
        | (TcoResetArgument { managedStr = Some(activeSlot), oldTemp = oldTemp } as argument) :: rest ->
            if strPredecessorIsReleased(argument)(state)
            then
                state
                |> emitGuardedAdtRelease(unlocatedInstruction)("rc_tco_drop_inactive")(activeSlot)(oldTemp)(SemString)
                |> dropStrBackEdgePredecessors(rest)
            else dropStrBackEdgePredecessors(rest)(state)
        | _argument :: rest -> dropStrBackEdgePredecessors(rest)(state)

let recursive emitRuntimeManagedBackEdgeStores (stores: List((Int, Int, Int))) (state: CoreLoweringState) =
    match stores with
        | [] -> state
        | (slot, activeSlot, temp) :: rest ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = activeTemp } ->
                    allocated
                    |> unlocatedInstruction(StoreLocal(slot)(temp))
                    |> unlocatedInstruction(LoadConstInt(activeTemp)(1))
                    |> unlocatedInstruction(StoreLocal(activeSlot)(activeTemp))
                    |> emitRuntimeManagedBackEdgeStores(rest)

// Stage 0's `TcoBackEdgeTryEmitRuntimeManagedReset`: every successor establishes its own
// reference first, the predecessors and the iteration-local owners are released, the fixed
// loop-entry watermark is restored (the successors live on the reference-counted heap, so the
// whole iteration's arena allocations go), each runtime-managed list slot is stored its successor
// with its active flag set, and the chunks above the watermark are reclaimed.
// Stage 0's `TcoBackEdgeEmitResetAndZeroReservations` after the restore: the reset reclaims any
// arena string reservation (it lives above the watermark), so the reservation slots of an affine
// accumulator whose successor stays in the arena are zeroed; a reference-counted reservation
// survives the reset and keeps its bounds.
// A reset argument whose parameter slot the frame placed on the reference-counted heap.
let resetArgumentIsManaged (argument: TcoResetArgument) =
    match (argument.managedStr, argument.managedAdt, argument.managedList) with
        | (Some(_str), _adt, _list) -> true
        | (None, Some(_adt), _list) -> true
        | (None, None, Some(_list)) -> true
        | _ -> false

let recursive zeroArenaReservations (arguments: List(TcoResetArgument)) (reservations: List((Int, Int, Int))) (zeroTemp: Int) (state: CoreLoweringState) =
    match arguments with
        | [] -> state
        | argument :: rest ->
            match lookupAffineReservation(argument.parameterSlot)(reservations) with
                | Some((reservationStart, reservationEnd)) ->
                    if argument.argumentRuntime || argument.passesThrough && resetArgumentIsManaged(argument)
                    then zeroArenaReservations(rest)(reservations)(zeroTemp)(state)
                    else
                        state
                        |> unlocatedInstruction(StoreLocal(reservationStart)(zeroTemp))
                        |> unlocatedInstruction(StoreLocal(reservationEnd)(zeroTemp))
                        |> zeroArenaReservations(rest)(reservations)(zeroTemp)
                | None -> zeroArenaReservations(rest)(reservations)(zeroTemp)(state)

let emitReservationZeroing (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match stateTcoLoopFrame(state) with
        | Some(CoreTcoLoopFrame { affineReservationSlots = [] }) -> state
        | Some(CoreTcoLoopFrame { affineReservationSlots = reservations }) ->
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = zeroTemp } ->
                    allocated
                    |> unlocatedInstruction(LoadConstInt(zeroTemp)(0))
                    |> zeroArenaReservations(arguments)(reservations)(zeroTemp)
        | None -> state

let recursive argumentIsManagedAt (ordinal: Int) (arguments: List(TcoResetArgument)) =
    match arguments with
        | [] -> false
        | argument :: rest ->
            if argument.ordinal == ordinal
            then resetArgumentIsManaged(argument)
            else argumentIsManagedAt(ordinal)(rest)

// Stage 0's `SuccessorPredatesTheLoop`: a blocker every self call hands on unchanged, or whose own
// consumed tail every self call passes, holds only parts of the value the loop was entered with,
// which no iteration's owned value can be.
let recursive argumentPredatesTheLoopAt (ordinal: Int) (arguments: List(TcoResetArgument)) =
    match arguments with
        | [] -> false
        | argument :: rest ->
            if argument.ordinal == ordinal
            then
                match argument.shape with
                    | TcoPassThroughShape -> true
                    | TcoConsumedTailShape -> true
                    | _ -> false
            else argumentPredatesTheLoopAt(ordinal)(rest)

// Every blocker of a predecessor was normalized onto the reference-counted heap on this path, or
// predates the loop.
let recursive predecessorBlockersClear (blockers: List(Int)) (arguments: List(TcoResetArgument)) (normalized: Bool) (unnamed: List(Int)) =
    match blockers with
        | [] -> true
        | ordinal :: rest -> (argumentPredatesTheLoopAt(ordinal)(arguments) || normalized && argumentIsManagedAt(ordinal)(arguments) || containsInt(ordinal)(unnamed)) && predecessorBlockersClear(rest)(arguments)(normalized)(unnamed)

// Stage 0's `EmitGeneralRcPredecessorDrops`: an earlier iteration's value is released once no
// successor can still name it, each successor that might (its blocker) having been normalized onto
// the reference-counted heap on this path, predating the loop, or naming nothing that can hold a
// part of the value.
let recursive emitGeneralRcPredecessorDrops (drops: List((Int, SemanticType, List(Int), List(Int), List(Int)))) (arguments: List(TcoResetArgument)) (normalized: Bool) (state: CoreLoweringState) =
    match drops with
        | [] -> state
        | (slot, semanticType, blockers, unnamedPlain, unnamedNormalized) :: rest ->
            let releasable =
                predecessorBlockersClear(blockers)(arguments)(normalized)(if normalized
                then unnamedNormalized
                else unnamedPlain)
            in
                if releasable
                then
                    state
                    |> emitGeneralRcOwnedSlotDrop(slot)(semanticType)
                    |> emitGeneralRcPredecessorDrops(rest)(arguments)(normalized)
                else emitGeneralRcPredecessorDrops(rest)(arguments)(normalized)(state)

let recursive isPredecessorSlot (slot: Int) (drops: List((Int, SemanticType, List(Int), List(Int), List(Int)))) =
    match drops with
        | [] -> false
        | (candidate, _semanticType, _blockers, _unnamedPlain, _unnamedNormalized) :: rest -> candidate == slot || isPredecessorSlot(slot)(rest)

let recursive flagAtOrdinal (ordinal: Int) (flags: List(Bool)) =
    match flags with
        | [] -> false
        | flag :: rest ->
            if ordinal == 0
            then flag
            else flagAtOrdinal(ordinal - 1)(rest)

let recursive ownedReadAtOrdinal (ordinal: Int) (slots: List(Int)) =
    match slots with
        | [] -> -1
        | slot :: rest ->
            if ordinal == 0
            then slot
            else ownedReadAtOrdinal(ordinal - 1)(rest)

let recursive resetArgumentAt (ordinal: Int) (arguments: List(TcoResetArgument)) =
    match arguments with
        | [] -> None
        | argument :: rest ->
            if argument.ordinal == ordinal
            then Some(argument)
            else resetArgumentAt(ordinal)(rest)

// Stage 0's `successorsOwnTheirParts` in `EmitGeneralRcBackEdgeDrops`: each successor is inline,
// handed on unchanged, a loop parameter's part in a tail-modulo-cons loop, or a parameter
// normalized onto the reference-counted heap on this path.
let recursive backEdgeSuccessorsOwnParts (arguments: List(TcoResetArgument)) (parts: List(Bool)) (tmc: Bool) (normalized: Bool) (state: CoreLoweringState) =
    match arguments with
        | [] -> true
        | argument :: rest -> (resultSurvivesReset(argument.argumentType)(state) || argument.shape == TcoPassThroughShape || tmc && flagAtOrdinal(argument.ordinal)(parts) || normalized && resetArgumentIsManaged(argument)) && backEdgeSuccessorsOwnParts(rest)(parts)(tmc)(normalized)(state)

// The per-successor half of stage 0's `NoSuccessorCanHoldPartOf`: each successor whose type may
// hold a part is handed on unchanged, a loop parameter's part in a tail-modulo-cons loop (or, for a
// value this iteration produced, any loop parameter's part, which predates it), or normalized.
let recursive typeBlockersExempt (blockers: List(Int)) (arguments: List(TcoResetArgument)) (parts: List(Bool)) (tmc: Bool) (useParameterParts: Bool) (normalized: Bool) =
    match blockers with
        | [] -> true
        | ordinal :: rest ->
            (match resetArgumentAt(ordinal)(arguments) with
                | Some(argument) -> argument.shape == TcoPassThroughShape || (tmc || useParameterParts) && flagAtOrdinal(ordinal)(parts) || normalized && resetArgumentIsManaged(argument)
                | None -> true) && typeBlockersExempt(rest)(arguments)(parts)(tmc)(useParameterParts)(normalized)

// Stage 0's `OwnedSlotsTakenBySuccessors`: a successor that is a plain read of an owned slot and
// whose parameter normalization took a reference of its own leaves the iteration's reference to
// be released.
let recursive takenOwnedSlots (arguments: List(TcoResetArgument)) (ownedReads: List(Int)) (normalized: Bool) =
    match arguments with
        | [] -> []
        | argument :: rest ->
            let slot = ownedReadAtOrdinal(argument.ordinal)(ownedReads)
            in
                if normalized && slot >= 0 && argument.shape != TcoPassThroughShape && !argument.argumentRuntime && resetArgumentIsManaged(argument)
                then slot :: takenOwnedSlots(rest)(ownedReads)(normalized)
                else takenOwnedSlots(rest)(ownedReads)(normalized)

let generalRcSlotReleased (reset: CoreTcoReset) (arguments: List(TcoResetArgument)) (taken: List(Int)) (tmc: Bool) (normalized: Bool) (fact: (Int, SemanticType, Int, List(Int))) =
    match fact with
        | (slot, _semanticType, _freshSlot, blockers) -> containsInt(slot)(taken) || typeBlockersExempt(blockers)(arguments)(reset.argumentParameterParts)(tmc)(false)(normalized)

let recursive emitReleasedGeneralRcSlots (facts: List((Int, SemanticType, Int, List(Int)))) released (state: CoreLoweringState) =
    match facts with
        | [] -> state
        | ((slot, semanticType, _freshSlot, _blockers) as fact) :: rest ->
            if released(fact)
            then
                state
                |> emitGeneralRcOwnedSlotDrop(slot)(semanticType)
                |> emitReleasedGeneralRcSlots(rest)(released)
            else emitReleasedGeneralRcSlots(rest)(released)(state)

// Stage 0's `EmitSkipUnlessReferenceCounted`: jumps to `skipLabel` unless the value is present and
// reference-counted.
let emitSkipUnlessReferenceCounted (valueTemp: Int) (skipLabel: Str) (state: CoreLoweringState) =
    match freshTempRun(3)(state) with
        | FreshTemp { state = allocated, temp = zeroTemp } ->
            allocated
            |> emit(LoadConstInt(zeroTemp)(0))
            |> emit(CmpIntNe(zeroTemp + 1)(valueTemp)(zeroTemp))
            |> emit(JumpIfFalse(zeroTemp + 1)(skipLabel))
            |> emit(IsReferenceCounted(zeroTemp + 2)(valueTemp))
            |> emit(JumpIfFalse(zeroTemp + 2)(skipLabel))

// Stage 0's `EmitSkipUnlessPresent`.
let emitSkipUnlessPresent (valueTemp: Int) (skipLabel: Str) (state: CoreLoweringState) =
    match freshTempRun(2)(state) with
        | FreshTemp { state = allocated, temp = zeroTemp } ->
            allocated
            |> emit(LoadConstInt(zeroTemp)(0))
            |> emit(CmpIntNe(zeroTemp + 1)(valueTemp)(zeroTemp))
            |> emit(JumpIfFalse(zeroTemp + 1)(skipLabel))

// The loop, shared-cell, and end labels of a list release walk, allocated in that order.
let freshListDropLabels (prefix: Str) (state: CoreLoweringState) =
    match freshLabel(prefix)(state) with
        | FreshLabel { state = loopLabelled, label = loopLabel } ->
            match freshLabel(prefix + "_shared")(loopLabelled) with
                | FreshLabel { state = sharedLabelled, label = sharedLabel } ->
                    match freshLabel(prefix + "_end")(sharedLabelled) with
                        | FreshLabel { state = labelled, label = endLabel } -> (loopLabel, sharedLabel, endLabel, labelled)

// Loads the walk's current cell from its slot and leaves the loop on the empty list; yields the
// state and the current cell temp.
let emitListDropTest (currentSlot: Int) (endLabel: Str) (state: CoreLoweringState) =
    match freshTempRun(3)(state) with
        | FreshTemp { state = reserved, temp = currentTemp } ->
            reserved
            |> emit(LoadLocal(currentTemp)(currentSlot))
            |> emit(LoadConstInt(currentTemp + 1)(0))
            |> emit(CmpIntNe(currentTemp + 2)(currentTemp)(currentTemp + 1))
            |> emit(JumpIfFalse(currentTemp + 2)(endLabel))
            |> (given (tested) -> (tested, currentTemp))

// Jumps to the shared exit unless the cell is uniquely owned.
let emitUniqueCellTest (cellTemp: Int) (sharedLabel: Str) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = reserved, temp = uniqueTemp } ->
            reserved
            |> emit(RcIsUnique(uniqueTemp)(cellTemp))
            |> emit(JumpIfFalse(uniqueTemp)(sharedLabel))

// Releases the unique cell and continues the walk through its tail.
let emitListDropAdvance (cellTemp: Int) (currentSlot: Int) (loopLabel: Str) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = reserved, temp = tailTemp } ->
            reserved
            |> emit(LoadMemOffset(tailTemp)(cellTemp)(8))
            |> emit(RcDrop(cellTemp)("List")(-1)(true)(false)(None))
            |> emit(StoreLocal(currentSlot)(tailTemp))
            |> emit(Jump(loopLabel))

let emitListDropSharedExit (cellTemp: Int) (sharedLabel: Str) (endLabel: Str) (state: CoreLoweringState) =
    state
    |> emit(Label(sharedLabel))
    |> emit(RcDrop(cellTemp)("List")(-1)(true)(false)(None))
    |> emit(Jump(endLabel))
    |> emit(Label(endLabel))

// Stage 0's `EmitRuntimeManagedListSpineDrop`: the spine's cells are released one by one, the
// elements left in place.
let emitRuntimeListSpineDrop (listTemp: Int) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = currentSlot } ->
            match allocated
            |> emit(StoreLocal(currentSlot)(listTemp))
            |> freshListDropLabels("rcdrop_list_spine") with
                | (loopLabel, sharedLabel, endLabel, labelled) ->
                    match labelled
                    |> emit(Label(loopLabel))
                    |> emitListDropTest(currentSlot)(endLabel) with
                        | (tested, currentTemp) ->
                            tested
                            |> emitUniqueCellTest(currentTemp)(sharedLabel)
                            |> emitListDropAdvance(currentTemp)(currentSlot)(loopLabel)
                            |> emitListDropSharedExit(currentTemp)(sharedLabel)(endLabel)

// Whether the owned call-result slot was handed to a callee that holds it only as whole unshared
// list elements (stage 0's `OwnedListHandedAsElementsOnly`).
let recursive takenOverConsumerAccepts (origin: Maybe(IrFunctionOrigin)) (slot: Int) (consumers: List((Maybe(IrFunctionOrigin), Int, Bool))) =
    match consumers with
        | [] -> false
        | (owner, candidate, accepts) :: rest ->
            if candidate == slot && owner == origin
            then accepts
            else takenOverConsumerAccepts(origin)(slot)(rest)

let recursive anyUnresolvedArgumentType (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match arguments with
        | [] -> false
        | TcoResetArgument { argumentType = argumentType } :: rest -> containsUnresolvedLayout(argumentType)(state) || anyUnresolvedArgumentType(rest)(state)

// Stage 0's `IsTakeOverCandidateList`: a list of a resolved element type that holds a heap value.
let isTakeOverCandidateList (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemList(element) -> !containsUnresolvedLayout(element)(state) && !resultSurvivesReset(element)(state)
        | _ -> false

// Stage 0's `TryTakeOverAtBackEdge` decision: an owned list this iteration stored, of a resolved element
// type, left behind a successor of unresolved layout, and handed to a callee holding only its elements.
let takesOverAtBackEdge (slot: Int) (semanticType: SemanticType) (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    isTakeOverCandidateList(semanticType)(state) && anyUnresolvedArgumentType(arguments)(state) && takenOverConsumerAccepts(stateActiveFunctionOrigin(state))(slot)(stateTakenOverConsumers(state))

let noteTakenOverRecordOfLoop (state: CoreLoweringState) =
    match stateTcoLoop(state) with
        | Some(CoreTcoLoop { selfName = selfName }) -> withStateTakenOverRecordNames(selfName :: stateTakenOverRecordNames(state))(state)
        | None -> state

let recursive slotSourceSpan (origin: Maybe(IrFunctionOrigin)) (slot: Int) (sources: List((Maybe(IrFunctionOrigin), Int, TextSpan))) =
    match sources with
        | [] -> None
        | (owner, candidate, span) :: rest ->
            if candidate == slot && owner == origin
            then Some(span)
            else slotSourceSpan(origin)(slot)(rest)

// The call whose result slot gives up its spine is a value of the function's own in the record proof.
let noteSpineFreedSlot (slot: Int) (state: CoreLoweringState) =
    match state
    |> stateTakenOverSlotSources
    |> slotSourceSpan(stateActiveFunctionOrigin(state))(slot) with
        | Some(span) -> withStateSpineFreedSpans(span :: stateSpineFreedSpans(state))(state)
        | None -> state

// Stage 0's `EmitTakeOverSpineRelease`: behind the slot's fresh flag, a present reference-counted list
// gives up its cells, its elements keeping the reference the successor's arena cells now own; the slot
// is emptied, the flag cleared, and the loop's function noted as handing a record over.
let emitTakeOverSpineRelease (slot: Int) (freshSlot: Int) (state: CoreLoweringState) =
    match freshLabel("taken_over_skip")(state) with
        | FreshLabel { state = labelled, label = skipLabel } ->
            match freshTempRun(2)(labelled) with
                | FreshTemp { state = allocated, temp = freshTempValue } ->
                    allocated
                    |> emit(LoadLocal(freshTempValue)(freshSlot))
                    |> emit(JumpIfFalse(freshTempValue)(skipLabel))
                    |> emit(LoadLocal(freshTempValue + 1)(slot))
                    |> emitSkipUnlessPresent(freshTempValue + 1)(skipLabel)
                    |> emitSkipUnlessReferenceCounted(freshTempValue + 1)(skipLabel)
                    |> emitRuntimeListSpineDrop(freshTempValue + 1)
                    |> (given (dropped: CoreLoweringState) ->
                        match freshTemp(dropped) with
                            | FreshTemp { state = zeroState, temp = zeroTemp } ->
                                zeroState
                                |> emit(LoadConstInt(zeroTemp)(0))
                                |> emit(StoreLocal(slot)(zeroTemp))
                                |> emit(Label(skipLabel)))
                    |> (given (skipped: CoreLoweringState) ->
                        match freshTemp(skipped) with
                            | FreshTemp { state = clearState, temp = clearTemp } ->
                                clearState
                                |> emit(LoadConstInt(clearTemp)(0))
                                |> emit(StoreLocal(freshSlot)(clearTemp)))
                    |> noteTakenOverRecordOfLoop
                    |> noteSpineFreedSlot(slot)

// A value this iteration stored is released behind its fresh flag when only the loop's own
// parameters could otherwise hold it; the flag is cleared either way, the value now being a
// successor's or gone.
let emitFreshGeneralRcSlotRelease (slot: Int) (semanticType: SemanticType) (freshSlot: Int) (releasable: Bool) (state: CoreLoweringState) =
    (let guarded =
        if releasable
        then
            match freshTemp(state) with
                | FreshTemp { state = allocated, temp = freshTempValue } ->
                    match allocated
                    |> emit(LoadLocal(freshTempValue)(freshSlot))
                    |> freshLabel("general_rc_owned_stale") with
                        | FreshLabel { state = labelled, label = staleLabel } ->
                            labelled
                            |> emit(JumpIfFalse(freshTempValue)(staleLabel))
                            |> emitGeneralRcOwnedSlotDrop(slot)(semanticType)
                            |> emit(Label(staleLabel))
        else state
    in
        match freshTemp(guarded) with
            | FreshTemp { state = zeroState, temp = zeroTemp } ->
                zeroState
                |> emit(LoadConstInt(zeroTemp)(0))
                |> emit(StoreLocal(freshSlot)(zeroTemp)))

// The successors that name nothing bound from the value of `slot`, on the path taken.
let recursive slotUnnamedSuccessors (slot: Int) (normalized: Bool) (unnamed: List((Int, List(Int), List(Int)))) =
    match unnamed with
        | [] -> []
        | (candidate, plain, onNormalized) :: rest ->
            if candidate == slot
            then
                if normalized
                then onNormalized
                else plain
            else slotUnnamedSuccessors(slot)(normalized)(rest)

let recursive blockersNamingTheValue (blockers: List(Int)) (unnamed: List(Int)) =
    match blockers with
        | [] -> []
        | ordinal :: rest ->
            if containsInt(ordinal)(unnamed)
            then blockersNamingTheValue(rest)(unnamed)
            else ordinal :: blockersNamingTheValue(rest)(unnamed)

// A fresh slot's blockers without its string parts, or its full blockers when none were recorded.
let recursive freshSlotBlockersOf (slot: Int) (fallback: List(Int)) (entries: List((Int, List(Int)))) =
    match entries with
        | [] -> fallback
        | (candidate, blockers) :: rest ->
            if candidate == slot
            then blockers
            else freshSlotBlockersOf(slot)(fallback)(rest)

let recursive emitFreshGeneralRcSlots (facts: List((Int, SemanticType, Int, List(Int)))) released (reset: CoreTcoReset) (arguments: List(TcoResetArgument)) (tmc: Bool) (normalized: Bool) (state: CoreLoweringState) =
    match facts with
        | [] -> state
        | ((slot, semanticType, freshSlot, blockers) as fact) :: rest ->
            if released(fact) || freshSlot < 0
            then emitFreshGeneralRcSlots(rest)(released)(reset)(arguments)(tmc)(normalized)(state)
            else
                if takesOverAtBackEdge(slot)(semanticType)(arguments)(state)
                then
                    state
                    |> emitTakeOverSpineRelease(slot)(freshSlot)
                    |> emitFreshGeneralRcSlots(rest)(released)(reset)(arguments)(tmc)(normalized)
                else
                    state
                    |> emitFreshGeneralRcSlotRelease(slot)(semanticType)(freshSlot)(typeBlockersExempt(freshSlotBlockersOf(slot)(blockers)(reset.generalRcFreshSlotBlockers))(arguments)(reset.argumentStrictParameterParts)(tmc)(true)(normalized) || typeBlockersExempt(reset.generalRcSlotUnnamed
                    |> slotUnnamedSuccessors(slot)(normalized)
                    |> blockersNamingTheValue(blockers))(arguments)(reset.argumentStrictParameterParts)(tmc)(true)(normalized))
                    |> emitFreshGeneralRcSlots(rest)(released)(reset)(arguments)(tmc)(normalized)

// Stage 0's `EmitGeneralRcBackEdgeDrops`: the earlier iterations' values first, then the owned
// values this iteration holds, all released once every successor owns its parts, and otherwise
// slot by slot: a value a normalized successor took, or that no successor can hold part of, is
// released outright, and one this iteration stored behind its fresh flag when only the loop's
// own parameters could hold it. The release carries no location, as the rest of the reset does.
let emitBackEdgeGeneralRcOwnedSlotDrops (reset: CoreTcoReset) (arguments: List(TcoResetArgument)) (normalized: Bool) (state: CoreLoweringState) =
    (let tmc =
        match stateTcoLoop(state) with
            | Some(loop) -> loop.tmcShapePresent
            | None -> false
    in
        let unlocated = emitGeneralRcPredecessorDrops(reset.generalRcPredecessorDrops)(arguments)(normalized)((state with currentSpan = None))
        in
            let released =
                if backEdgeSuccessorsOwnParts(arguments)(reset.argumentParameterParts)(tmc)(normalized)(unlocated)
                then
                    emitReleasedGeneralRcSlots(reset.generalRcSlotFacts)(given (_fact) -> true)(unlocated)
                else
                    let taken = takenOwnedSlots(arguments)(reset.argumentOwnedSlots)(normalized)
                    in
                        let releasedFact = generalRcSlotReleased(reset)(arguments)(taken)(tmc)(normalized)
                        in
                            unlocated
                            |> emitReleasedGeneralRcSlots(reset.generalRcSlotFacts)(releasedFact)
                            |> emitFreshGeneralRcSlots(reset.generalRcSlotFacts)(releasedFact)(reset)(arguments)(tmc)(normalized)
            in released with currentSpan = state.currentSpan)

// The reset and its reclaim, each skipped while a one-shot post is pending; the reference-counted
// successors' stores between them run either way.
let emitGuardedRuntimeManagedTcoRestore (reset: CoreTcoReset) (arguments: List(TcoResetArgument)) preRestoreSlot (state: CoreLoweringState) =
    match beginLivePostsGuardBy(unlocatedInstruction)(state) with
        | (guarded, skipLabel) ->
            guarded
            |> unlocatedInstruction(RestoreArenaState(reset.fixedCursorSlot)(reset.fixedEndSlot)(preRestoreSlot)(false))
            |> emitReservationZeroing(arguments)
            |> endLivePostsGuardBy(unlocatedInstruction)(skipLabel)

let emitGuardedRuntimeManagedTcoReclaim (reset: CoreTcoReset) preRestoreSlot (state: CoreLoweringState) =
    match beginLivePostsGuardBy(unlocatedInstruction)(state) with
        | (guarded, skipLabel) ->
            guarded
            |> unlocatedInstruction(ReclaimArenaChunks(reset.fixedEndSlot)(preRestoreSlot)(false))
            |> endLivePostsGuardBy(unlocatedInstruction)(skipLabel)

let emitRuntimeManagedTcoReset (reset: CoreTcoReset) (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = preRestoreSlot } ->
            match allocated
            |> withdrawBorrowedSuccessorRetains(arguments)(resetArgumentParameters(arguments))
            |> normalizeRuntimeManagedBackEdgeArguments(arguments)([]) with
                | (normalized, stores) ->
                    normalized
                    |> dropRuntimeManagedBackEdgePredecessors(arguments)
                    |> emitTransferredRootDrops(reset.transferredRootDrops)
                    |> emitLateCopiedCellHeadDrops(reverse(reset.lateCopiedCellHeads))(arguments)
                    |> emitResolvedOwnedDrops(reset.ownedDrops)
                    |> emitBackEdgeGeneralRcOwnedSlotDrops(reset)(arguments)(true)
                    |> emitGuardedRuntimeManagedTcoRestore(reset)(arguments)(preRestoreSlot)
                    |> emitRuntimeManagedBackEdgeStores(stores)
                    |> emitGuardedRuntimeManagedTcoReclaim(reset)(preRestoreSlot)

// What carries an arena loop parameter's successor across the back edge's reset, stage 0's
// `GetTcoCopyOutKind`: a string copies by its length, a big integer by its limb count, a
// same-arity scalar-field ADT by its cell size, a deep-copyable ADT or tuple through the
// synthesized clone, and a list's top cell with its head copied by the head's kind.
type TcoCompactionCopy =
    | TcoNoCompactionCopy
    | TcoShallowCompactionCopy(Int)
    | TcoDeepCompactionCopy
    | TcoListCellCompactionCopy(ListHeadCopyKind)

let tcoCompactionCopyOf (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> TcoShallowCompactionCopy(-1)
        | SemBigInt -> TcoShallowCompactionCopy(copyOutBigIntSize)
        | SemTuple(_elements) as tuple ->
            match heapFactsOf(tuple)(state) with
                | HeapLayoutFacts { arenaDeepCopySupported = true } -> TcoDeepCompactionCopy
                | _ -> TcoNoCompactionCopy
        | SemNamed(_symbolId, name, _arguments) as named ->
            match heapFactsOf(named)(state) with
                | HeapLayoutFacts { structuralCopy = ShallowCopy } ->
                    state
                    |> shallowAdtCopySizeBytes(name)
                    |> TcoShallowCompactionCopy
                | HeapLayoutFacts { arenaDeepCopySupported = true } -> TcoDeepCompactionCopy
                | _ -> TcoNoCompactionCopy
        | _ -> TcoNoCompactionCopy

let hasCompactionCopy (copy: TcoCompactionCopy) =
    match copy with
        | TcoNoCompactionCopy -> false
        | _ -> true

let isDeepCompactionCopy (copy: TcoCompactionCopy) =
    match copy with
        | TcoDeepCompactionCopy -> true
        | _ -> false

// Stage 0's `GetTcoListCopyOutKind` and `TcoBackEdgeArgCopyOutKind`: a list of scalars copies its
// 16-byte top cell, a list of strings or of scalar lists copies its top cell with the head, and a
// list of deep-copyable elements is cloned whole, which costs its length at every back edge and is
// taken only when the successor rebuilt the list this iteration.
let tcoListCompactionCopyOf (element: SemanticType) (argument: TcoResetArgument) (state: CoreLoweringState) =
    (let resolvedElement = resolveType(state)(element)
    in
        let wholeClone =
            match heapFactsOf(resolvedElement)(state) with
                | HeapLayoutFacts { arenaDeepCopySupported = true } ->
                    if shapeIsFreshList(argument.argumentExpression)
                    then TcoDeepCompactionCopy
                    else TcoNoCompactionCopy
                | _ -> TcoNoCompactionCopy
        in
            if resultSurvivesReset(resolvedElement)(state)
            then TcoShallowCompactionCopy(16)
            else
                match resolvedElement with
                    | SemString -> TcoListCellCompactionCopy(StringListHead)
                    | SemList(inner) ->
                        if resultSurvivesReset(inner)(state)
                        then TcoListCellCompactionCopy(InnerListHead)
                        else wholeClone
                    | _ -> wholeClone)

let tcoArgumentCompactionCopyOf (argument: TcoResetArgument) (state: CoreLoweringState) =
    match resolveType(state)(argument.argumentType) with
        | SemList(element) -> tcoListCompactionCopyOf(element)(argument)(state)
        | _ -> tcoCompactionCopyOf(argument.argumentType)(state)

let argumentTypeResolved (argument: TcoResetArgument) (state: CoreLoweringState) =
    match resolveType(state)(argument.argumentType) with
        | SemVariable(_id) -> false
        | _ -> true

// An argument that needs no copy at the reset: a scalar, a resource handle, or the loop's own
// unchanged value, which sits below the watermark (stage 0's `ArgResetSafe`); an argument whose
// type is still unresolved is never reset-safe.
let recursive backEdgeParameterName (ordinal: Int) (names: List(Str)) =
    match names with
        | [] -> None
        | name :: rest ->
            if ordinal == 0
            then Some(name)
            else backEdgeParameterName(ordinal - 1)(rest)

// The root and last argument of a call spine, without the general spine collector defined further
// down: this runs while a back edge's reset is being resolved.
let recursive backEdgeCallRootAndAccumulator (expression: Expr) (lastArgument: Maybe(Expr)) =
    match unspanArgument(expression) with
        | ExprCall(function, argument, _isSugar, _layout) ->
            match lastArgument with
                | None -> backEdgeCallRootAndAccumulator(function)(Some(argument))
                | Some(_alreadyLast) -> backEdgeCallRootAndAccumulator(function)(lastArgument)
        | root -> (root, lastArgument)

// Stage 0's `stableAccArg`: a fully-reusing specialization rewrites its accumulator in place below
// the loop watermark, so the value the back edge threads is the same cell the iteration started
// with and a plain reset keeps it live. The call is recognized structurally — the callee's
// specialization was accepted as fully reusing, and its own accumulator argument is this loop
// parameter — rather than by remembering the call node, which has no identity here.
let backEdgeArgumentIsInPlaceReuse (argument: TcoResetArgument) (state: CoreLoweringState) =
    match stateTcoLoop(state) with
        | None -> false
        | Some(CoreTcoLoop { parameterNames = parameterNames }) ->
            match backEdgeParameterName(argument.ordinal)(parameterNames) with
                | None -> false
                | Some(parameterName) ->
                    match backEdgeCallRootAndAccumulator(argument.argumentExpression)(None) with
                        | (ExprVar(callee), Some(accumulatorArgument)) ->
                            containsName(callee)(stateFullyReusingCallees(state)) && (match unspanArgument(accumulatorArgument) with
                                | ExprVar(name) -> name == parameterName
                                | _ -> false)
                        | _ -> false

// An affine accumulator (`acc + f(i)`, consumed once as the leftmost leaf of its own append) is
// extended in place at the arena tip, the case the per-iteration reset is built around, so a
// pending operator default there still counts as the `Int` it falls back to. Without the reset
// every iteration would copy the whole accumulator and the loop's arena would grow quadratically.
let pendingAffineAccumulatorSurvives (argument: TcoResetArgument) (state: CoreLoweringState) =
    match stateTcoLoop(state) with
        | Some(CoreTcoLoop { affineOrdinals = ordinals }) -> containsInt(argument.ordinal)(ordinals) && resultSurvivesReset(argument.argumentType)(state)
        | None -> false

let compactionArgumentSurvives (argument: TcoResetArgument) (state: CoreLoweringState) = backEdgeArgumentSurvivesReset(argument.argumentType)(state) || pendingAffineAccumulatorSurvives(argument)(state) || backEdgeArgumentIsInPlaceReuse(argument)(state) || argumentTypeResolved(argument)(state) && (isResourceHandle(argument.argumentType)(state) || argument.shape == TcoPassThroughShape)

let recursive allArgumentsSurvivePlainReset (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match arguments with
        | [] -> true
        | argument :: rest -> compactionArgumentSurvives(argument)(state) && allArgumentsSurvivePlainReset(rest)(state)

// Stage 0's `TcoBackEdgeAllArgsCopyable`: every argument survives the reset on its own or has a
// copy, and a list argument is either cloned whole or a single fresh cell over a loop parameter;
// any other list shape gives the reset up.
let argumentCompactable (argument: TcoResetArgument) (state: CoreLoweringState) =
    compactionArgumentSurvives(argument)(state) || (match tcoArgumentCompactionCopyOf(argument)(state) with
        | TcoNoCompactionCopy -> false
        | copy ->
            match resolveType(state)(argument.argumentType) with
                | SemList(_element) -> isDeepCompactionCopy(copy) || argument.singleFreshCons
                | _ -> true)

let recursive allArgumentsCompactable (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match arguments with
        | [] -> true
        | argument :: rest -> argumentCompactable(argument)(state) && allArgumentsCompactable(rest)(state)

// Stage 0's `TcoBackEdgeUseFixedWatermark`: the reset goes back to the fixed loop-entry watermark
// unless a list argument keeps only its top cell, whose tail sits below the per-iteration watermark
// and would be overwritten by a fixed-mark reset; that loop resets to the advancing watermark.
let recursive compactionUsesFixedWatermark (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match arguments with
        | [] -> true
        | argument :: rest ->
            (compactionArgumentSurvives(argument)(state) || (match resolveType(state)(argument.argumentType) with
                | SemList(_element) ->
                    state
                    |> tcoArgumentCompactionCopyOf(argument)
                    |> isDeepCompactionCopy
                | _ -> true)) && compactionUsesFixedWatermark(rest)(state)

// Stage 0's `TcoBackEdgeCompactionCopiesRuntimeManagedList`: a list parameter on the
// reference-counted heap whose successor the copy-out would compact beside an arena sibling's.
let recursive compactionCopiesManagedList (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match arguments with
        | [] -> false
        | argument :: rest ->
            (match argument.managedList with
                | Some(_managed) ->
                    argument.shape != TcoPassThroughShape && (state |> tcoArgumentCompactionCopyOf(argument) |> hasCompactionCopy)
                | None -> false) || compactionCopiesManagedList(rest)(state)

// The inline clone of an arena value (stage 0's `EmitDeepCopy`), its copier functions synthesized
// into the program once per type; answers the temp holding the clone.
let emitTcoDeepCopy (sourceTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { nextTemp = nextTemp, nextLocal = nextLocal, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
            match synthesizeDeepCopy(sourceTemp)(resolveType(state)(semanticType))(dropperTypesOf(state))(cache)(nextTemp)(nextLocal)(lambdaId)(labelId)(IndependentClone) with
                | (synthesis, resultTemp) -> (spliceInlineReleaseWith(unlocatedInstruction)(synthesis)(state), resultTemp)

let emitCompactionShallowCopy (sourceTemp: Int) (sizeBytes: Int) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = destTemp } ->
            (unlocatedInstruction(CopyOutArena(destTemp)(sourceTemp)(sizeBytes)(false)(ArenaTcoCompaction)(None))(allocated), destTemp)

// A list's top cell copied with its head, the string or scalar list the head names copied too.
let emitCompactionListCellCopy (sourceTemp: Int) (headCopy: ListHeadCopyKind) (state: CoreLoweringState) =
    match freshTemp(state) with
        | FreshTemp { state = allocated, temp = destTemp } ->
            (unlocatedInstruction(CopyOutTcoListCell(destTemp)(sourceTemp)(headCopy)(ArenaTcoCompaction))(allocated), destTemp)

let nextCompactionLabel (prefix: Str) (state: CoreLoweringState) = (prefix + "_" + Ashes.Text.fromInt(state.nextLambdaId), (state with nextLambdaId = state.nextLambdaId + 1))

// `count` consecutive temps, answering the first.
let reserveResetTemps (count: Int) (state: CoreLoweringState) = (state.nextTemp, (state with nextTemp = state.nextTemp + count))

// Stage 0's `TcoBackEdgeNetReservationSpans`: an arena string reservation is live capacity, not
// garbage, so its span is netted out of the growth before the threshold test.
let recursive netReservationSpans (arguments: List(TcoResetArgument)) (reservations: List((Int, Int, Int))) (growthTemp: Int) (state: CoreLoweringState) =
    match arguments with
        | [] -> (state, growthTemp)
        | argument :: rest ->
            match lookupAffineReservation(argument.parameterSlot)(reservations) with
                | Some((reservationStart, reservationEnd)) ->
                    if argument.argumentRuntime
                    then netReservationSpans(rest)(reservations)(growthTemp)(state)
                    else
                        match reserveResetTemps(4)(state) with
                            | (startTemp, allocated) ->
                                allocated
                                |> unlocatedInstruction(LoadLocal(startTemp)(reservationStart))
                                |> unlocatedInstruction(LoadLocal(startTemp + 1)(reservationEnd))
                                |> unlocatedInstruction(SubInt(startTemp + 2)(startTemp + 1)(startTemp))
                                |> unlocatedInstruction(SubInt(startTemp + 3)(growthTemp)(startTemp + 2))
                                |> netReservationSpans(rest)(reservations)(startTemp + 3)
                | None -> netReservationSpans(rest)(reservations)(growthTemp)(state)

// Stage 0's `TcoBackEdgeEmitCompactionCheck`: the whole accumulator is copied only once the arena
// has grown past twice the live size recorded at the last compaction plus slack, or the cursor
// left the watermark's chunk; every other back edge keeps allocating above the watermark.
// Answers the skip label the compaction ends with.
let emitCompactionCheck (frame: CoreTcoLoopFrame) (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = cursorState, local = currentCursorSlot } ->
            match freshLocal(cursorState) with
                | FreshLocal { state = endState, local = currentEndSlot } ->
                    match reserveResetTemps(3)(endState) with
                        | (currentTemp, growthState) ->
                            match growthState
                            |> unlocatedInstruction(SaveArenaState(currentCursorSlot)(currentEndSlot)(false))
                            |> unlocatedInstruction(LoadLocal(currentTemp)(currentCursorSlot))
                            |> unlocatedInstruction(LoadLocal(currentTemp + 1)(frame.fixedCursorSlot))
                            |> unlocatedInstruction(SubInt(currentTemp + 2)(currentTemp)(currentTemp + 1))
                            |> netReservationSpans(arguments)(frame.affineReservationSlots)(currentTemp + 2) with
                                | (nettedState, growthTemp) ->
                                    match reserveResetTemps(10)(nettedState) with
                                        | (liveTemp, thresholdState) ->
                                            match nextCompactionLabel("tco_compact_skip")(thresholdState) with
                                                | (skipLabel, labeledState) ->
                                                    labeledState
                                                    |> unlocatedInstruction(LoadLocal(liveTemp)(frame.compactionSizeSlot))
                                                    |> unlocatedInstruction(LoadConstInt(liveTemp + 1)(1))
                                                    |> unlocatedInstruction(ShlInt(liveTemp + 2)(liveTemp)(liveTemp + 1))
                                                    |> unlocatedInstruction(LoadConstInt(liveTemp + 3)(4096))
                                                    |> unlocatedInstruction(AddInt(liveTemp + 4)(liveTemp + 2)(liveTemp + 3))
                                                    |> unlocatedInstruction(CmpIntGt(liveTemp + 5)(growthTemp)(liveTemp + 4))
                                                    |> unlocatedInstruction(LoadLocal(liveTemp + 6)(frame.fixedEndSlot))
                                                    |> unlocatedInstruction(LoadLocal(liveTemp + 7)(currentEndSlot))
                                                    |> unlocatedInstruction(CmpIntNe(liveTemp + 8)(liveTemp + 7)(liveTemp + 6))
                                                    |> unlocatedInstruction(OrInt(liveTemp + 9)(liveTemp + 5)(liveTemp + 8))
                                                    |> unlocatedInstruction(JumpIfFalse(liveTemp + 9)(skipLabel))
                                                    |> (given (checked: CoreLoweringState) -> (checked, skipLabel))

// Phase A of stage 0's two-pass copy-out: every heap argument is copied up above the current
// cursor, a deep clone twice (the second clone starts a full clone above the watermark, so the
// down copy never overlaps its source), so the reset below cannot reclaim its bytes.
let recursive emitCompactionUpCopies (arguments: List(TcoResetArgument)) (state: CoreLoweringState) (reversedCopies: List((TcoResetArgument, Int, TcoCompactionCopy))) =
    match arguments with
        | [] -> (state, reverse(reversedCopies))
        | argument :: rest ->
            if compactionArgumentSurvives(argument)(state)
            then emitCompactionUpCopies(rest)(state)(reversedCopies)
            else
                match tcoArgumentCompactionCopyOf(argument)(state) with
                    | TcoNoCompactionCopy -> emitCompactionUpCopies(rest)(state)(reversedCopies)
                    | TcoDeepCompactionCopy ->
                        match emitTcoDeepCopy(argument.argumentTemp)(argument.argumentType)(state) with
                            | (firstState, firstTemp) ->
                                match emitTcoDeepCopy(firstTemp)(argument.argumentType)(firstState) with
                                    | (secondState, secondTemp) -> emitCompactionUpCopies(rest)(secondState)((argument, secondTemp, TcoDeepCompactionCopy) :: reversedCopies)
                    | TcoShallowCompactionCopy(sizeBytes) ->
                        match emitCompactionShallowCopy(argument.argumentTemp)(sizeBytes)(state) with
                            | (copied, upTemp) -> emitCompactionUpCopies(rest)(copied)((argument, upTemp, TcoShallowCompactionCopy(sizeBytes)) :: reversedCopies)
                    | TcoListCellCompactionCopy(headCopy) ->
                        match emitCompactionListCellCopy(argument.argumentTemp)(headCopy)(state) with
                            | (copied, upTemp) -> emitCompactionUpCopies(rest)(copied)((argument, upTemp, TcoListCellCompactionCopy(headCopy)) :: reversedCopies)

// An affine string accumulator's down copy reserves (an in-place append of the empty string: the
// slots were just zeroed, so the fallback reserves twice the size and records fresh bounds), so
// the accumulator and its headroom sit exactly where the rebase puts the watermark.
let emitCompactionReservingCopy (upTemp: Int) (reservationStart: Int) (reservationEnd: Int) (state: CoreLoweringState) =
    match internString("")(state) with
        | StringInterning { state = interned, label = emptyLabel } ->
            match reserveResetTemps(2)(interned) with
                | (emptyTemp, allocated) ->
                    allocated
                    |> unlocatedInstruction(LoadConstStr(emptyTemp)(emptyLabel))
                    |> unlocatedInstruction(ConcatStrTip(emptyTemp + 1)(upTemp)(emptyTemp)(reservationStart)(reservationEnd)(false))
                    |> (given (reserved: CoreLoweringState) -> (reserved, emptyTemp + 1))

// Phase B: each up copy is copied down to the watermark and stored into its parameter slot.
let recursive emitCompactionDownCopies (copies: List((TcoResetArgument, Int, TcoCompactionCopy))) (reservations: List((Int, Int, Int))) (state: CoreLoweringState) =
    match copies with
        | [] -> state
        | (argument, upTemp, copy) :: rest ->
            match (copy, lookupAffineReservation(argument.parameterSlot)(reservations)) with
                | (TcoDeepCompactionCopy, _reservation) ->
                    match emitTcoDeepCopy(upTemp)(argument.argumentType)(state) with
                        | (copied, downTemp) ->
                            copied
                            |> unlocatedInstruction(StoreLocal(argument.parameterSlot)(downTemp))
                            |> emitCompactionDownCopies(rest)(reservations)
                | (TcoShallowCompactionCopy(-1), Some((reservationStart, reservationEnd))) ->
                    match emitCompactionReservingCopy(upTemp)(reservationStart)(reservationEnd)(state) with
                        | (reserved, downTemp) ->
                            reserved
                            |> unlocatedInstruction(StoreLocal(argument.parameterSlot)(downTemp))
                            |> emitCompactionDownCopies(rest)(reservations)
                | (TcoShallowCompactionCopy(sizeBytes), _reservation) ->
                    match emitCompactionShallowCopy(upTemp)(sizeBytes)(state) with
                        | (copied, downTemp) ->
                            copied
                            |> unlocatedInstruction(StoreLocal(argument.parameterSlot)(downTemp))
                            |> emitCompactionDownCopies(rest)(reservations)
                | (TcoListCellCompactionCopy(headCopy), _reservation) ->
                    match emitCompactionListCellCopy(upTemp)(headCopy)(state) with
                        | (copied, downTemp) ->
                            copied
                            |> unlocatedInstruction(StoreLocal(argument.parameterSlot)(downTemp))
                            |> emitCompactionDownCopies(rest)(reservations)
                | (TcoNoCompactionCopy, _reservation) -> emitCompactionDownCopies(rest)(reservations)(state)

// Stage 0's `TcoBackEdgeEmitCompactionRecord`: the compacted live size is recorded for the next
// threshold, or, when the down copy overflowed into a new chunk, the fixed watermark is rebased to
// that chunk's allocation start (read from the chunk footer, which holds the chunk's base) and the
// live size restarts there.
let emitCompactionRecord (frame: CoreTcoLoopFrame) (skipLabel: Str) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = cursorState, local = afterCursorSlot } ->
            match freshLocal(cursorState) with
                | FreshLocal { state = endState, local = afterEndSlot } ->
                    match reserveResetTemps(10)(endState) with
                        | (afterTemp, tempState) ->
                            match nextCompactionLabel("tco_compact_rebase")(tempState) with
                                | (rebaseLabel, rebaseState) ->
                                    match nextCompactionLabel("tco_compact_recorded")(rebaseState) with
                                        | (recordedLabel, labeledState) ->
                                            labeledState
                                            |> unlocatedInstruction(SaveArenaState(afterCursorSlot)(afterEndSlot)(false))
                                            |> unlocatedInstruction(LoadLocal(afterTemp)(afterCursorSlot))
                                            |> unlocatedInstruction(LoadLocal(afterTemp + 1)(frame.fixedCursorSlot))
                                            |> unlocatedInstruction(SubInt(afterTemp + 2)(afterTemp)(afterTemp + 1))
                                            |> unlocatedInstruction(LoadLocal(afterTemp + 3)(afterEndSlot))
                                            |> unlocatedInstruction(LoadLocal(afterTemp + 4)(frame.fixedEndSlot))
                                            |> unlocatedInstruction(CmpIntEq(afterTemp + 5)(afterTemp + 3)(afterTemp + 4))
                                            |> unlocatedInstruction(JumpIfFalse(afterTemp + 5)(rebaseLabel))
                                            |> unlocatedInstruction(StoreLocal(frame.compactionSizeSlot)(afterTemp + 2))
                                            |> unlocatedInstruction(Jump(recordedLabel))
                                            |> unlocatedInstruction(Label(rebaseLabel))
                                            |> unlocatedInstruction(LoadMemOffset(afterTemp + 6)(afterTemp + 3)(0))
                                            |> unlocatedInstruction(LoadConstInt(afterTemp + 7)(8))
                                            |> unlocatedInstruction(AddInt(afterTemp + 8)(afterTemp + 6)(afterTemp + 7))
                                            |> unlocatedInstruction(StoreLocal(frame.fixedCursorSlot)(afterTemp + 8))
                                            |> unlocatedInstruction(StoreLocal(frame.fixedEndSlot)(afterTemp + 3))
                                            |> unlocatedInstruction(SubInt(afterTemp + 9)(afterTemp)(afterTemp + 8))
                                            |> unlocatedInstruction(StoreLocal(frame.compactionSizeSlot)(afterTemp + 9))
                                            |> unlocatedInstruction(Label(recordedLabel))
                                            |> unlocatedInstruction(Label(skipLabel))

// Stage 0's `EmitTcoBackEdgeArenaBlock` beyond the plain reset: the amortized fixed-watermark
// compaction of a loop whose heap arguments all have a whole-value copy. Under the threshold check
// the heap arguments are copied up above the cursor, the arena is reset to the fixed loop-entry
// watermark (the arena reservations of an arena string successor zeroed), the copies are copied
// down onto the watermark and stored into their slots, the chunks above are reclaimed, and the
// live size is recorded. The whole block is skipped while a one-shot post is pending.
let emitCompactingTcoReset (frame: CoreTcoLoopFrame) (arguments: List(TcoResetArgument)) (preRestoreSlot: Int) (state: CoreLoweringState) =
    match beginLivePostsGuardBy(unlocatedInstruction)(state) with
        | (guarded, postsSkipLabel) ->
            match emitCompactionCheck(frame)(arguments)(guarded) with
                | (checked, skipLabel) ->
                    match emitCompactionUpCopies(arguments)(checked)([]) with
                        | (copiedUp, copies) ->
                            copiedUp
                            |> unlocatedInstruction(RestoreArenaState(frame.fixedCursorSlot)(frame.fixedEndSlot)(preRestoreSlot)(false))
                            |> emitReservationZeroing(arguments)
                            |> emitCompactionDownCopies(copies)(frame.affineReservationSlots)
                            |> unlocatedInstruction(ReclaimArenaChunks(frame.fixedEndSlot)(preRestoreSlot)(false))
                            |> emitCompactionRecord(frame)(skipLabel)
                            |> endLivePostsGuardBy(unlocatedInstruction)(postsSkipLabel)

// The same two-pass copy-out against the advancing per-iteration watermark, for a loop whose list
// argument keeps only its top cell: every back edge copies that cell, so nothing is amortized and
// no live size is recorded. Skipped as a whole while a one-shot post is pending.
let emitAdvancingCompactingTcoReset (frame: CoreTcoLoopFrame) (arguments: List(TcoResetArgument)) (preRestoreSlot: Int) (state: CoreLoweringState) =
    match beginLivePostsGuardBy(unlocatedInstruction)(state) with
        | (guarded, postsSkipLabel) ->
            match emitCompactionUpCopies(arguments)(guarded)([]) with
                | (copiedUp, copies) ->
                    copiedUp
                    |> unlocatedInstruction(RestoreArenaState(frame.arenaCursorSlot)(frame.arenaEndSlot)(preRestoreSlot)(false))
                    |> emitReservationZeroing(arguments)
                    |> emitCompactionDownCopies(copies)(frame.affineReservationSlots)
                    |> unlocatedInstruction(ReclaimArenaChunks(frame.arenaEndSlot)(preRestoreSlot)(false))
                    |> endLivePostsGuardBy(unlocatedInstruction)(postsSkipLabel)

let emitLocatedDeepCopy (sourceTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    match state with
        | CoreLoweringState { nextTemp = nextTemp, nextLocal = nextLocal, nextLambdaId = lambdaId, nextLabelId = labelId, programState = CoreProgramState { dropperLabels = cache } } ->
            match synthesizeDeepCopy(sourceTemp)(resolveType(state)(semanticType))(dropperTypesOf(state))(cache)(nextTemp)(nextLocal)(lambdaId)(labelId)(IndependentClone) with
                | (synthesis, resultTemp) -> (spliceInlineReleaseWith(emit)(synthesis)(state), resultTemp)

let releaseDemotedSource (releasesSource: Bool) (sourceTemp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    if releasesSource
    then emitOwnedValueChildDrop(sourceTemp)(semanticType)(state)
    else state

// Stage 0's `EmitArenaCopyOfReferenceCounted`: an arena copy of a value that may be
// reference-counted, releasing the original when the caller owned it; a value already outside the
// reference-counted heap is used as it is.
let emitArenaCopyOfReferenceCounted (sourceTemp: Int) (semanticType: SemanticType) (releasesSource: Bool) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = slotted, local = resultSlot } ->
            match slotted
            |> emit(StoreLocal(resultSlot)(sourceTemp))
            |> freshTemp with
                | FreshTemp { state = tested, temp = referenceCountedTemp } ->
                    match freshLabel("rc_arena_demote_done")(tested) with
                        | FreshLabel { state = labelled, label = doneLabel } ->
                            match labelled
                            |> emit(IsReferenceCounted(referenceCountedTemp)(sourceTemp))
                            |> emit(JumpIfFalse(referenceCountedTemp)(doneLabel))
                            |> emitLocatedDeepCopy(sourceTemp)(semanticType) with
                                | (copied, copyTemp) ->
                                    match copied
                                    |> emit(StoreLocal(resultSlot)(copyTemp))
                                    |> releaseDemotedSource(releasesSource)(sourceTemp)(semanticType)
                                    |> emit(Label(doneLabel))
                                    |> freshTemp with
                                        | FreshTemp { state = loaded, temp = resultTemp } ->
                                            (emit(LoadLocal(resultTemp)(resultSlot))(loaded), resultTemp)

let isFunctionSemanticType (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(_argument, _result, _row) -> true
        | _ -> false

// Stage 0's `StoresArenaCopyForKeptSuccessor`: an owned binding's value an aggregate built for a
// kept arena successor stores. The arena keeps the successor until the loop ends while the binding
// is released at the back edge, so the aggregate takes an arena copy, which lives as long as the
// successor, instead of a retain nothing would release.
let storesArenaCopyForKeptSuccessor (semanticType: SemanticType) (state: CoreLoweringState) =
    match (stateArenaKeptSuccessorLetLoop(state), stateTcoLoop(state)) with
        | (Some(name), Some(loop)) ->
            if name == loop.selfName && generalRcEnabled(state)
            then
                match resolveType(state)(semanticType) with
                    | resolved -> resultSurvivesReset(resolved)(state) == false && isFunctionSemanticType(resolved) == false && containsUnresolvedLayout(resolved)(state) == false && isGeneralRcAdmissible(resolved)([])(state)
            else false
        | _ -> false

// The reference a transferred child of an owned binding takes: a retain, or an arena copy for a
// kept arena successor.
let keptSuccessorTransferRetain (temp: Int) (semanticType: SemanticType) (state: CoreLoweringState) =
    if storesArenaCopyForKeptSuccessor(semanticType)(state)
    then
        match emitArenaCopyOfReferenceCounted(temp)(semanticType)(false)(state) with
            | (copied, copyTemp) -> success(copyTemp)(semanticType)(copied)
    else emitTransferRetain(temp)(semanticType)(state)

// Whether a transferred child is the read of a binding that still owns its reference, whole or by
// a heap-typed field, the reads `retainTransferredChild` retains.
let readsTransferredOwner (child: Expr) (semanticType: SemanticType) (state: CoreLoweringState) =
    match unspanArgument(child) with
        | ExprVar(name) ->
            match (liveRuntimeOwnerSlot(name)(state), patternOwnerBinding(name)(state)) with
                | (Some(_slot), None) -> true
                | _ -> false
        | ExprQualifiedVar(owner, _field) ->
            if resultSurvivesReset(semanticType)(state)
            then false
            else
                match (liveRuntimeOwnerSlot(owner)(state), patternOwnerBinding(owner)(state)) with
                    | (Some(_slot), None) -> true
                    | _ -> false
        | _ -> false

// `retainTransferredChild` with the arena copy a kept arena successor's aggregate takes.
let retainSuccessorTransferredChild (child: Expr) (transfers: Bool) (lowered: LoweredCoreValue) =
    match lowered with
        | LoweredCoreValue { state = state, temp = temp, semanticType = semanticType, error = None } ->
            if transfers && readsTransferredOwner(child)(semanticType)(state) && storesArenaCopyForKeptSuccessor(semanticType)(state)
            then keptSuccessorTransferRetain(temp)(semanticType)(state)
            else retainTransferredChild(child)(transfers)(lowered)
        | _ -> lowered

// Stage 0's `CopyTransferRetainedSuccessorsIntoArena` for one parameter: a back edge that keeps
// the iteration's arena hands an arena parameter a successor it retained out of an owner the
// iteration releases. The parameter releases nothing, so it takes an arena copy instead and the
// retained reference is released after the copy.
let copyTransferRetainedSuccessor (argument: TcoResetArgument) (retained: Bool) (state: CoreLoweringState) =
    match argument with
        | TcoResetArgument { parameterSlot = slot, argumentType = argumentType, managedList = None, managedAdt = None, managedStr = None } ->
            if retained && !resultSurvivesReset(argumentType)(state) && !containsUnresolvedLayout(argumentType)(state) && isGeneralRcAdmissible(argumentType)([])(state) && hasBoundedCopy(argumentType)([])(state)
            then
                // The back edge's block is unlocated, as stage 0 splices it.
                match freshTemp((state with currentSpan = None)) with
                    | FreshTemp { state = allocated, temp = successorTemp } ->
                        match allocated
                        |> emit(LoadLocal(successorTemp)(slot))
                        |> emitArenaCopyOfReferenceCounted(successorTemp)(argumentType)(true) with
                            | (copied, copyTemp) ->
                                emit(StoreLocal(slot)(copyTemp))(copied) with currentSpan = state.currentSpan
            else state
        | _ -> state

let recursive copyTransferRetainedSuccessors (arguments: List(TcoResetArgument)) (retained: List(Bool)) (state: CoreLoweringState) =
    match (arguments, retained) with
        | (argument :: rest, flag :: restFlags) ->
            state
            |> copyTransferRetainedSuccessor(argument)(flag)
            |> copyTransferRetainedSuccessors(rest)(restFlags)
        | _ -> state

// Whether a successor temp is a transfer retain: the temp a runtime-managed duplicate defines
// (the retain `retainTransferredChild` takes of a live owner's read).
let recursive isTransferRetainIn (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = RcDup(target, _source, runtimeManaged, _mayBeEmpty) } :: rest ->
            if target == temp
            then runtimeManaged
            else isTransferRetainIn(temp)(rest)
        | _ :: rest -> isTransferRetainIn(temp)(rest)

// The fields of an arena constructor cell, back to the allocation that made it: its tag, whether it
// is tagless, and each (field index, stored temp). None when the cell is reference-counted or was
// not allocated in this body.
let recursive allocatedArenaCellFields (cell: Int) (instructions: List(IrInstruction)) (fields: List((Int, Int))) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = AllocAdt(target, tag, _fieldCount, runtimeManaged, tagless) } :: rest ->
            if target == cell
            then
                if runtimeManaged
                then None
                else Some((tag, tagless, fields))
            else allocatedArenaCellFields(cell)(rest)(fields)
        | IrInstruction { instruction = SetAdtField(pointer, fieldIndex, source, _tagless) } :: rest ->
            if pointer == cell
            then allocatedArenaCellFields(cell)(rest)((fieldIndex, source) :: fields)
            else allocatedArenaCellFields(cell)(rest)(fields)
        | _ :: rest -> allocatedArenaCellFields(cell)(rest)(fields)

// Stage 0's `CopyTransferDuplicatedChildrenIntoArena` for one field: a value of bounded, resolved
// type takes an arena copy, stored back into the cell, and its retain is released.
let copyTransferDuplicatedField (cell: Int) (tagless: Bool) (fieldIndex: Int) (source: Int) (fieldType: Maybe(SemanticType)) (state: CoreLoweringState) =
    match fieldType with
        | Some(semanticType) ->
            if isTransferRetainIn(source)(state.reversedInstructions) && !resultSurvivesReset(semanticType)(state) && !containsUnresolvedLayout(semanticType)(state) && hasBoundedCopy(semanticType)([])(state)
            then
                match emitArenaCopyOfReferenceCounted(source)(semanticType)(true)(state) with
                    | (copied, copyTemp) ->
                        emit(SetAdtField(cell)(fieldIndex)(copyTemp)(tagless))(copied)
            else state
        | None -> state

let recursive copyTransferDuplicatedFields (cell: Int) (typeName: Str) (typeArguments: List(SemanticType)) (tag: Int) (tagless: Bool) (fields: List((Int, Int))) (state: CoreLoweringState) =
    match fields with
        | [] -> state
        | (fieldIndex, source) :: rest ->
            state
            |> copyTransferDuplicatedField(cell)(tagless)(fieldIndex)(source)(constructorFieldTypeAt(typeName)(typeArguments)(tag)(fieldIndex)(stateConstructorsOfType(typeName)(state))(state))
            |> copyTransferDuplicatedFields(cell)(typeName)(typeArguments)(tag)(tagless)(rest)

// Stage 0's `CopyTransferDuplicatedChildrenIntoArena`: an arena parameter's successor that is a
// constructor cell built for it, whose fields hold references retained for the transfer out of an
// owner the iteration releases (`Some(label)`). Neither the arena cell nor the parameter releases
// them, so each field takes an arena copy and its retain is released.
let copyTransferDuplicatedChildren (argument: TcoResetArgument) (state: CoreLoweringState) =
    match argument with
        | TcoResetArgument { argumentTemp = cell, argumentType = argumentType, managedList = None, managedAdt = None, managedStr = None } ->
            match resolveType(state)(argumentType) with
                | SemNamed(_symbolId, typeName, typeArguments) ->
                    match allocatedArenaCellFields(cell)(state.reversedInstructions)([]) with
                        | Some((tag, tagless, fields)) ->
                            (state with currentSpan = None)
                            |> copyTransferDuplicatedFields(cell)(typeName)(typeArguments)(tag)(tagless)(fields)
                            |> (given (copied: CoreLoweringState) -> copied with currentSpan = state.currentSpan)
                        | None -> state
                | _ -> state
        | _ -> state

let recursive copyAllTransferDuplicatedChildren (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match arguments with
        | [] -> state
        | argument :: rest ->
            state
            |> copyTransferDuplicatedChildren(argument)
            |> copyAllTransferDuplicatedChildren(rest)

// Whether a successor at one of these ordinals is a call's result left in the arena: neither
// handed on unchanged nor of a type a reset leaves alone.
let recursive arenaCallResultAmong (ordinals: List(Int)) (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match ordinals with
        | [] -> false
        | ordinal :: rest ->
            (match resetArgumentAt(ordinal)(arguments) with
                | Some(argument) -> argument.shape != TcoPassThroughShape && !resultSurvivesReset(argument.argumentType)(state)
                | None -> false) || arenaCallResultAmong(rest)(arguments)(state)

// Stage 0's `ArenaSuccessorMayHoldPartOf`: on a back edge that normalizes no successor, a call's
// result left in the arena may share an iteration's owned value's cells without references of its
// own (a generic callee rebuilds a list around the tail it was given). A value such a successor
// reaches, and that a successor's type can hold a part of, is left unreleased, as an owned slot of
// the contract is in the same place.
let recursive ownedDropsLeftByArenaSuccessors (drops: List((Int, Str))) (facts: List((Int, List(Int), List(Int)))) (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match (drops, facts) with
        | (drop :: rest, (_slot, blockers, reachedBy) :: restFacts) ->
            if !typeBlockersExempt(blockers)(arguments)([])(false)(false)(false) && arenaCallResultAmong(reachedBy)(arguments)(state)
            then ownedDropsLeftByArenaSuccessors(rest)(restFacts)(arguments)(state)
            else drop :: ownedDropsLeftByArenaSuccessors(rest)(restFacts)(arguments)(state)
        | _ -> drops

// The back edge of a loop the runtime-managed reset does not admit: the iteration-local owners
// are released, then the per-iteration watermark is restored when every argument survives a reset
// on its own (stage 0's `TcoBackEdgeTryEmitPlainReset`), the arena compacted when every heap
// argument has a copy (to the fixed watermark, or to the advancing one when a list keeps only its
// top cell), and the iteration's allocations left in place otherwise.
let emitArenaTcoReset (reset: CoreTcoReset) (frame: CoreTcoLoopFrame) (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = allocated, local = preRestoreSlot } ->
            allocated
            |> emitTransferredRootDrops(reset.transferredRootDrops)
            |> (given (rootsReleased: CoreLoweringState) ->
                emitResolvedOwnedDrops(ownedDropsLeftByArenaSuccessors(reset.ownedDrops)(reset.ownedDropFacts)(arguments)(rootsReleased))(rootsReleased))
            |> emitBackEdgeGeneralRcOwnedSlotDrops(reset)(arguments)(false)
            |> (given (released: CoreLoweringState) ->
                if allArgumentsSurvivePlainReset(arguments)(released)
                then emitGuardedRestoreAndReclaimBy(unlocatedInstruction)(reset.arenaCursorSlot)(reset.arenaEndSlot)(preRestoreSlot)(released)
                else
                    if allArgumentsCompactable(arguments)(released)
                    then
                        if compactionUsesFixedWatermark(arguments)(released)
                        then emitCompactingTcoReset(frame)(arguments)(preRestoreSlot)(released)
                        else emitAdvancingCompactingTcoReset(frame)(arguments)(preRestoreSlot)(released)
                    else
                        released
                        |> copyTransferRetainedSuccessors(arguments)(reset.argumentTransferRetained)
                        |> copyAllTransferDuplicatedChildren(arguments))

// Stage 0's `TcoBackEdgeTryEmitRuntimeManagedSuccessorsWithoutReset`: a loop that can neither
// reset its arena plainly nor copy every argument out still owns the list and aggregate parameters
// it placed on the reference-counted heap. Each successor takes a reference of its own and the
// predecessor is released, as the reset path does, only without the reset; the iteration's owned
// drops follow the successors, which may borrow from them.
let tcoBackEdgeKeepsSuccessorsWithoutReset (arguments: List(TcoResetArgument)) (state: CoreLoweringState) = generalRcEnabled(state) && anyManagedListArgument(arguments) && !allArgumentsSurvivePlainReset(arguments)(state) && (!allArgumentsCompactable(arguments)(state) || compactionCopiesManagedList(arguments)(state))

let emitRuntimeManagedSuccessorsWithoutReset (reset: CoreTcoReset) (arguments: List(TcoResetArgument)) (state: CoreLoweringState) =
    match freshLocal(state) with
        | FreshLocal { state = allocated } ->
            match allocated
            |> withdrawBorrowedSuccessorRetains(arguments)(resetArgumentParameters(arguments))
            |> normalizeRuntimeManagedBackEdgeArguments(arguments)([]) with
                | (normalized, stores) ->
                    normalized
                    |> dropRuntimeManagedBackEdgePredecessors(arguments)
                    |> emitTransferredRootDrops(reset.transferredRootDrops)
                    |> emitLateCopiedCellHeadDrops(reverse(reset.lateCopiedCellHeads))(arguments)
                    |> emitResolvedOwnedDrops(reset.ownedDrops)
                    |> emitBackEdgeGeneralRcOwnedSlotDrops(reset)(arguments)(true)
                    |> emitRuntimeManagedBackEdgeStores(stores)

let emitResolvedTcoReset (reset: CoreTcoReset) (state: CoreLoweringState) =
    match (stateTcoLoopFrame(state), stateTcoLoop(state)) with
        | (Some(frame), Some(loop)) ->
            state
            |> tcoResetArguments(0)(frame.parameterSlots)(reset.argumentTypes)(reset.argumentTemps)(reset.oldTemps)(reset.argumentRuntime)(loop.argumentShapes)(reset.argumentExpressions)(reset.argumentSingleFreshCons)(reset.argumentPassThrough)(frame.runtimeManagedListSlots)(frame.runtimeManagedAdtSlots)(frame.runtimeManagedStrSlots)
            |> (given (arguments: List(TcoResetArgument)) ->
                if tcoBackEdgeCanEmitRuntimeManagedReset(loop)(arguments)(state)
                then emitRuntimeManagedTcoReset(reset)(arguments)(state)
                else
                    if tcoBackEdgeKeepsSuccessorsWithoutReset(arguments)(state)
                    then emitRuntimeManagedSuccessorsWithoutReset(reset)(arguments)(state)
                    else
                        state
                        |> dropStrBackEdgePredecessors(arguments)
                        |> emitArenaTcoReset(reset)(frame)(arguments))
        | _ -> emitPlainTcoReset(reset)(state)
