// Synthesizes the reference-counted droppers stage 0 places behind a label: a structural owner
// dropper (`__rcdrop_structural_N`) releasing a whole list, tuple, or aggregate graph from one
// callable helper, so a placed `RcDrop` stays a single instruction while the release stays
// complete; and a constructor-switching ADT dropper (`__rcdrop_N`) for a recursive or owned-child
// ADT, which the structural walk calls for such a child. Each dropper is a complete `IrFunction`
// with env-and-arg parameters (slot 0 the environment, slot 1 the value) returning the constant 0,
// built from its own temp and local counters; only the label and lambda counters are shared with
// the enclosing lowering so the helper names stay unique program-wide.
//
// Invariants:
// - A dropper is synthesized once per pretty-printed type; the cache maps the type to its label,
//   and a recursive ADT dropper finds its own label in the cache before its body is emitted.
// - A unique cell releases its owned children before its own header; a shared cell is only
//   decremented and keeps its children.
// - A list is walked iteratively through a local slot, never by recursion on the tail.
// - Every named type is classified under a symbol id of its own, keyed by name, so two types the
//   lowering registered under the same id are never conflated.
// - A child whose release the walk does not recognize is left in place rather than released
//   partially.

import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.FunctionOrigins
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.TaglessAdtLayout
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
export (
    type DropperLabelCache(..),
    type DropperSynthesis(..),
    value emptyDropperLabelCache,
    value structuralReleaseNeedsHelper,
    value ownedChildrenOf,
    value isScalarResultType,
    value synthesizeStructuralOwnerDropper,
    value synthesizeRuntimeManagedAdtDropper,
    type OwnedReleasePlan(..),
    type InlineReleaseSynthesis(..),
    value synthesizeOwnedAggregateRelease,
)

// The labels already synthesized, keyed by the pretty-printed type they release.
type DropperLabelCache =
    | structuralLabels: List((Str, Str))
    | adtLabels: List((Str, Str))
    deriving {Eq, Show}

let emptyDropperLabelCache = DropperLabelCache(structuralLabels = [], adtLabels = [])

// The outcome of one synthesis request: the helper's label (`None` when the release needs no
// helper), the cache and counters to carry forward, and the functions synthesized by the request
// in completion order.
type DropperSynthesis =
    | label: Maybe(Str)
    | cache: DropperLabelCache
    | functions: List(IrFunction)
    | nextLambdaId: Int
    | nextLabelId: Int

// The build state of one synthesized body together with the facts every nested synthesis shares:
// the label cache, the functions completed so far, the two shared counters, and the
// classification environment.
type DropperBody =
    | reversedInstructions: List(IrInstruction)
    | nextTemp: Int
    | nextLocal: Int
    | cache: DropperLabelCache
    | functions: List(IrFunction)
    | nextLambdaId: Int
    | nextLabelId: Int
    | environment: TypeEnvironment

let recursive schemeResultTypeName (body: SemanticType) =
    match body with
        | SemFunction(_parameter, result, _row) -> schemeResultTypeName(result)
        | SemNamed(_symbolId, name, _arguments) -> Some(name)
        | _ -> None

let recursive lookupTypeId (name: Str) (ids: List((Str, Int))) =
    match ids with
        | [] -> None
        | (candidate, id) :: rest ->
            if candidate == name
            then Some(id)
            else lookupTypeId(name)(rest)

// One symbol id per named type declared by a constructor, in first-seen order.
let recursive namedTypeIds (definitions: List(ConstructorInferenceDefinition)) (reversedIds: List((Str, Int))) =
    match definitions with
        | [] -> reverse(reversedIds)
        | ConstructorInferenceDefinition { scheme = TypeScheme { body = body } } :: rest ->
            match schemeResultTypeName(body) with
                | None -> namedTypeIds(rest)(reversedIds)
                | Some(name) ->
                    match lookupTypeId(name)(reversedIds) with
                        | Some(_id) -> namedTypeIds(rest)(reversedIds)
                        | None -> namedTypeIds(rest)((name, length(reversedIds)) :: reversedIds)

let recursive renumberType (ids: List((Str, Int))) (semanticType: SemanticType) =
    match semanticType with
        | SemList(element) ->
            element
            |> renumberType(ids)
            |> SemList
        | SemTuple(elements) ->
            elements
            |> map(renumberType(ids))
            |> SemTuple
        | SemFunction(parameter, result, row) ->
            SemFunction(renumberType(ids)(parameter))(renumberType(ids)(result))(row)
        | SemNamed(symbolId, name, arguments) ->
            match lookupTypeId(name)(ids) with
                | Some(id) ->
                    arguments
                    |> map(renumberType(ids))
                    |> SemNamed(id)(name)
                | None ->
                    arguments
                    |> map(renumberType(ids))
                    |> SemNamed(symbolId)(name)
        | other -> other

let renumberDefinition (ids: List((Str, Int))) (definition: ConstructorInferenceDefinition) =
    match definition with
        | ConstructorInferenceDefinition { name = name, scheme = TypeScheme { quantified = quantified, body = body, constraints = constraints }, fieldNames = fieldNames } ->
            ConstructorInferenceDefinition(
                name = name,
                scheme = TypeScheme(quantified = quantified, body = renumberType(ids)(body), constraints = constraints),
                fieldNames = fieldNames
            )

let dropperEnvironment (ids: List((Str, Int))) (definitions: List(ConstructorInferenceDefinition)) =
    emptyTypeEnvironment(Unit) with constructors = map(renumberDefinition(ids))(definitions)

let openDropperBody (definitions: List(ConstructorInferenceDefinition)) (cache: DropperLabelCache) (nextLambdaId: Int) (nextLabelId: Int) =
    (let ids = namedTypeIds(definitions)([])
    in
        (ids, DropperBody(
            reversedInstructions = [],
            nextTemp = 0,
            nextLocal = 0,
            cache = cache,
            functions = [],
            nextLambdaId = nextLambdaId,
            nextLabelId = nextLabelId,
            environment = dropperEnvironment(ids)(definitions)
        )))

let freshDropperTemp (body: DropperBody) =
    match body with
        | DropperBody { nextTemp = nextTemp } -> (nextTemp, (body with nextTemp = nextTemp + 1))

let freshDropperLocal (body: DropperBody) =
    match body with
        | DropperBody { nextLocal = nextLocal } -> (nextLocal, (body with nextLocal = nextLocal + 1))

let freshDropperLabel (prefix: Str) (body: DropperBody) =
    match body with
        | DropperBody { nextLabelId = nextLabelId } -> (prefix + "_" + Ashes.Text.fromInt(nextLabelId), (body with nextLabelId = nextLabelId + 1))

let emitDropper (kind: IrInstructionKind) (body: DropperBody) =
    match body with
        | DropperBody { reversedInstructions = instructions } -> body with reversedInstructions = IrInstruction(instruction = kind, location = None) :: instructions

let bodyEnvironment (body: DropperBody) =
    match body with
        | DropperBody { environment = environment } -> environment

// The owned type name a whole-value `RcDrop` releases under.
let dropperTypeName (semanticType: SemanticType) =
    match semanticType with
        | SemString -> "String"
        | SemBytes -> "Bytes"
        | SemBigInt -> "BigInt"
        | SemList(_element) -> "List"
        | SemTuple(_elements) -> "Tuple"
        | SemNamed(_symbolId, name, _arguments) -> name
        | _ -> "Value"

let emitRcDrop (valueTemp: Int) (typeName: Str) (body: DropperBody) =
    emitDropper(RcDrop(valueTemp)(typeName)(-1)(true)(false)(None))(body)

// The whole-value release of a named type, under the type's own name.
let emitTypeDrop (valueTemp: Int) (named: SemanticType) (body: DropperBody) =
    emitRcDrop(valueTemp)(dropperTypeName(named))(body)

// `RcIsUnique` on the value, branching to `sharedLabel` when it is shared.
let emitUniqueTest (valueTemp: Int) (sharedLabel: Str) (body: DropperBody) =
    match freshDropperTemp(body) with
        | (uniqueTemp, uniqueBody) ->
            uniqueBody
            |> emitDropper(RcIsUnique(uniqueTemp)(valueTemp))
            |> emitDropper(JumpIfFalse(uniqueTemp)(sharedLabel))

let emitReturnZero (body: DropperBody) =
    match freshDropperTemp(body) with
        | (resultTemp, resultBody) ->
            resultBody
            |> emitDropper(LoadConstInt(resultTemp)(0))
            |> emitDropper(Return(resultTemp))

// Slot 0 is the implicit environment and slot 1 the value; the value is loaded into the first temp.
let openSynthesizedValue (body: DropperBody) =
    match freshDropperLocal(body) with
        | (_environmentSlot, environmentBody) ->
            match freshDropperLocal(environmentBody) with
                | (argumentSlot, argumentBody) ->
                    match freshDropperTemp(argumentBody) with
                        | (valueTemp, valueBody) ->
                            (valueTemp, emitDropper(LoadLocal(valueTemp)(argumentSlot))(valueBody))

// A fresh body for a nested synthesis, sharing the cache, functions, counters, and environment.
let beginSynthesizedBody (body: DropperBody) = body with reversedInstructions = [], nextTemp = 0, nextLocal = 0

// Completes the synthesized function from `inner` and returns `outer` carrying the cache,
// functions, and shared counters forward.
let finishSynthesizedBody (label: Str) (origin: IrFunctionOrigin) (outer: DropperBody) (inner: DropperBody) =
    match inner with
        | DropperBody { reversedInstructions = instructions, nextTemp = tempCount, nextLocal = localCount, cache = cache, functions = functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId } ->
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
                    lifetimesPlaced = true
                )
            in outer with cache = cache, functions = append(functions)([function]), nextLambdaId = nextLambdaId, nextLabelId = nextLabelId

let recursive lookupLabel (key: Str) (labels: List((Str, Str))) =
    match labels with
        | [] -> None
        | (candidate, label) :: rest ->
            if candidate == key
            then Some(label)
            else lookupLabel(key)(rest)

let cachedStructuralLabel (key: Str) (body: DropperBody) =
    match body with
        | DropperBody { cache = DropperLabelCache { structuralLabels = labels } } -> lookupLabel(key)(labels)

let cachedAdtLabel (key: Str) (body: DropperBody) =
    match body with
        | DropperBody { cache = DropperLabelCache { adtLabels = labels } } -> lookupLabel(key)(labels)

let registerStructuralLabel (key: Str) (body: DropperBody) =
    match body with
        | DropperBody { cache = DropperLabelCache { structuralLabels = structural, adtLabels = adt }, nextLambdaId = nextLambdaId } ->
            let label = "__rcdrop_structural_" + Ashes.Text.fromInt(nextLambdaId)
            in (label, (body with cache = DropperLabelCache(structuralLabels = (key, label) :: structural, adtLabels = adt), nextLambdaId = nextLambdaId + 1))

let registerAdtLabel (key: Str) (body: DropperBody) =
    match body with
        | DropperBody { cache = DropperLabelCache { structuralLabels = structural, adtLabels = adt }, nextLambdaId = nextLambdaId } ->
            let label = "__rcdrop_" + Ashes.Text.fromInt(nextLambdaId)
            in (label, (body with cache = DropperLabelCache(structuralLabels = structural, adtLabels = (key, label) :: adt), nextLambdaId = nextLambdaId + 1))

// The constructor names of the named type, in declaration order.
let recursive constructorsNamed (typeName: Str) (definitions: List(ConstructorInferenceDefinition)) =
    match definitions with
        | [] -> []
        | ConstructorInferenceDefinition { name = name, scheme = TypeScheme { body = body } } :: rest ->
            match schemeResultTypeName(body) with
                | Some(candidate) ->
                    if candidate == typeName
                    then name :: constructorsNamed(typeName)(rest)
                    else constructorsNamed(typeName)(rest)
                | None -> constructorsNamed(typeName)(rest)

let namedTypeConstructors (named: SemanticType) (body: DropperBody) =
    match (named, bodyEnvironment(body)) with
        | (SemNamed(_symbolId, name, _arguments), TypeEnvironment { constructors = definitions }) -> constructorsNamed(name)(definitions)
        | _ -> []

let firstConstructorName (named: SemanticType) (body: DropperBody) =
    match namedTypeConstructors(named)(body) with
        | [] -> None
        | first :: _rest -> Some(first)

let definitionScheme (definition: ConstructorInferenceDefinition) =
    match definition with
        | ConstructorInferenceDefinition { scheme = scheme } -> scheme

let recursive schemesOfType (typeName: Str) (definitions: List(ConstructorInferenceDefinition)) =
    match definitions with
        | [] -> []
        | ConstructorInferenceDefinition { scheme = TypeScheme { body = body } as scheme } :: rest ->
            if schemeResultTypeName(body) == Some(typeName)
            then scheme :: schemesOfType(typeName)(rest)
            else schemesOfType(typeName)(rest)

// Whether the named type's cell carries no tag word, decided by the OPT-24 rule over the
// constructors in scope: a sole constructor with fields that is neither compiler-provided nor a
// resource handle nor resource-bearing (a user-declared resource or zero-cost type is not visible
// here and stays tagged).
let typeIsTagless (named: SemanticType) (body: DropperBody) =
    match (named, bodyEnvironment(body)) with
        | (SemNamed(_symbolId, name, _arguments), TypeEnvironment { constructors = definitions }) ->
            match schemesOfType(name)(definitions) with
                | scheme :: [] ->
                    isTaglessAdtConstructor(isBuiltinResourceTypeName)(map(definitionScheme)(definitions))(false)(scheme)
                | _ -> false
        | _ -> false

// Whether a described child is owned (needs a drop) and belongs to the given constructor (`None`
// for a list or tuple child).
let childBelongsTo (constructorName: Maybe(Str)) (child: HeapLayoutChild) =
    match child with
        | HeapLayoutChild { dropKind = NoChildDrop } -> false
        | HeapLayoutChild { constructorName = owner } ->
            match (owner, constructorName) with
                | (None, None) -> true
                | (Some(candidate), Some(expected)) -> candidate == expected
                | _ -> false

let ownedChildrenOf (constructorName: Maybe(Str)) (facts: HeapLayoutFacts) =
    match facts with
        | HeapLayoutFacts { children = children } ->
            filter(childBelongsTo(constructorName))(children)

let ownedChildren (constructorName: Maybe(Str)) (semanticType: SemanticType) (body: DropperBody) =
    body
    |> bodyEnvironment
    |> classifyHeapLayout(semanticType)
    |> ownedChildrenOf(constructorName)

// `Result(Str, Int)` and `Result(Str, Float)` carry no owned payload worth walking: a single
// release of the cell is their whole drop.
let isScalarResultType (named: SemanticType) =
    match named with
        | SemNamed(_symbolId, "Result", SemString :: SemInt :: []) -> true
        | SemNamed(_symbolId, "Result", SemString :: SemFloat :: []) -> true
        | _ -> false

// A recursive-copy or owned-child ADT is released by its own constructor-switching dropper.
let usesRecursiveDropper (named: SemanticType) (body: DropperBody) =
    match body
    |> bodyEnvironment
    |> classifyHeapLayout(named) with
        | HeapLayoutFacts { runtimeOwnedChildAdtSupported = true } -> true
        | _ ->
            body
            |> bodyEnvironment
            |> heapRuntimeRecursiveCopyAdtLayout(named)

let recursive allocateConstructorLabels (names: List(Str)) (body: DropperBody) =
    match names with
        | [] -> ([], body)
        | name :: rest ->
            match freshDropperLabel("rcdrop_ctor")(body) with
                | (label, labelBody) ->
                    match allocateConstructorLabels(rest)(labelBody) with
                        | (blocks, blocksBody) -> ((label, name) :: blocks, blocksBody)

let recursive switchCases (blocks: List((Str, Str))) (tag: Int) =
    match blocks with
        | [] -> []
        | (label, _name) :: rest -> IrSwitchCase(tag = tag, label = label) :: switchCases(rest)(tag + 1)

let listLabels (body: DropperBody) =
    match freshDropperLabel("rcdrop_list")(body) with
        | (loopLabel, loopBody) ->
            match freshDropperLabel("rcdrop_list_shared")(loopBody) with
                | (sharedLabel, sharedBody) ->
                    match freshDropperLabel("rcdrop_list_end")(sharedBody) with
                        | (endLabel, endBody) -> (loopLabel, sharedLabel, endLabel, endBody)

// Loads the current cell from its slot and leaves the loop when it is the empty list.
let emitListLoopTest (currentSlot: Int) (endLabel: Str) (body: DropperBody) =
    match freshDropperTemp(body) with
        | (currentTemp, currentBody) ->
            match freshDropperTemp(currentBody) with
                | (zeroTemp, zeroBody) ->
                    match freshDropperTemp(zeroBody) with
                        | (nonEmptyTemp, nonEmptyBody) ->
                            nonEmptyBody
                            |> emitDropper(LoadLocal(currentTemp)(currentSlot))
                            |> emitDropper(LoadConstInt(zeroTemp)(0))
                            |> emitDropper(CmpIntNe(nonEmptyTemp)(currentTemp)(zeroTemp))
                            |> emitDropper(JumpIfFalse(nonEmptyTemp)(endLabel))
                            |> (given (tested) -> (currentTemp, tested))

// Releases a unique cell and continues the walk through its tail.
let emitListTailAdvance (currentTemp: Int) (currentSlot: Int) (loopLabel: Str) (body: DropperBody) =
    match freshDropperTemp(body) with
        | (tailTemp, tailBody) ->
            tailBody
            |> emitDropper(LoadMemOffset(tailTemp)(currentTemp)(8))
            |> emitRcDrop(currentTemp)("List")
            |> emitDropper(StoreLocal(currentSlot)(tailTemp))
            |> emitDropper(Jump(loopLabel))

let emitListSharedExit (currentTemp: Int) (sharedLabel: Str) (endLabel: Str) (body: DropperBody) =
    body
    |> emitDropper(Label(sharedLabel))
    |> emitRcDrop(currentTemp)("List")
    |> emitDropper(Jump(endLabel))
    |> emitDropper(Label(endLabel))

// Stage 0's `EmitAdtTag`: the constructor tag of a cell, read from its tag word, or loaded as the
// literal tag of the sole constructor when the type's cell carries no tag word.
let emitConstructorTagRead (tagTemp: Int) (valueTemp: Int) (named: SemanticType) (body: DropperBody) =
    if typeIsTagless(named)(body)
    then
        emitDropper(LoadConstInt(tagTemp)(0))(body)
    else
        emitDropper(GetAdtTag(tagTemp)(valueTemp))(body)

// Releases a runtime-managed child by its type: a tuple or list is walked, a named type dropped
// by constructor, and a string-like leaf released as one allocation. A unique list cell releases
// an owned head before continuing through its tail; a shared cell keeps head and tail and is only
// decremented.
let recursive emitChildDrop (valueTemp: Int) (childType: SemanticType) (body: DropperBody) =
    match childType with
        | SemTuple(elements) -> emitTupleDrop(valueTemp)(elements)(body)
        | SemList(element) -> emitListDrop(valueTemp)(element)(body)
        | SemNamed(_symbolId, _name, _arguments) -> emitAdtDrop(valueTemp)(childType)(body)
        | SemString -> emitRcDrop(valueTemp)("String")(body)
        | SemBytes -> emitRcDrop(valueTemp)("Bytes")(body)
        | SemBigInt -> emitRcDrop(valueTemp)("BigInt")(body)
        | _ -> body
and emitListDrop (listTemp: Int) (elementType: SemanticType) (body: DropperBody) =
    match freshDropperLocal(body) with
        | (currentSlot, slotBody) ->
            match slotBody
            |> emitDropper(StoreLocal(currentSlot)(listTemp))
            |> listLabels with
                | (loopLabel, sharedLabel, endLabel, labelBody) ->
                    match labelBody
                    |> emitDropper(Label(loopLabel))
                    |> emitListLoopTest(currentSlot)(endLabel) with
                        | (currentTemp, testedBody) ->
                            testedBody
                            |> emitUniqueTest(currentTemp)(sharedLabel)
                            |> emitListHeadDrop(currentTemp)(elementType)
                            |> emitListTailAdvance(currentTemp)(currentSlot)(loopLabel)
                            |> emitListSharedExit(currentTemp)(sharedLabel)(endLabel)
and emitListHeadDrop (cellTemp: Int) (elementType: SemanticType) (body: DropperBody) =
    if canArenaResetLayout(elementType)
    then body
    else
        match freshDropperTemp(body) with
            | (headTemp, headBody) ->
                headBody
                |> emitDropper(LoadMemOffset(headTemp)(cellTemp)(0))
                |> emitChildDrop(headTemp)(elementType)
and emitTupleDrop (valueTemp: Int) (elements: List(SemanticType)) (body: DropperBody) =
    match freshDropperLabel("rc_drop_tuple_shared")(body) with
        | (sharedLabel, labelBody) ->
            match ownedChildren(None)(SemTuple(elements))(labelBody) with
                | [] -> emitRcDrop(valueTemp)("Tuple")(labelBody)
                | children ->
                    labelBody
                    |> emitUniqueTest(valueTemp)(sharedLabel)
                    |> emitTupleChildDrops(valueTemp)(children)
                    |> emitDropper(Label(sharedLabel))
                    |> emitRcDrop(valueTemp)("Tuple")
and emitTupleChildDrops (valueTemp: Int) (children: List(HeapLayoutChild)) (body: DropperBody) =
    match children with
        | [] -> body
        | HeapLayoutChild { fieldIndex = index, childType = childType } :: rest ->
            match freshDropperTemp(body) with
                | (childTemp, childBody) ->
                    childBody
                    |> emitDropper(LoadMemOffset(childTemp)(valueTemp)(index * 8))
                    |> emitChildDrop(childTemp)(childType)
                    |> emitTupleChildDrops(valueTemp)(rest)
and emitAdtDrop (valueTemp: Int) (named: SemanticType) (body: DropperBody) =
    if isScalarResultType(named)
    then emitTypeDrop(valueTemp)(named)(body)
    else
        if usesRecursiveDropper(named)(body)
        then emitRecursiveAdtDrop(valueTemp)(named)(body)
        else emitFirstConstructorDrop(valueTemp)(named)(body)
and emitFirstConstructorDrop (valueTemp: Int) (named: SemanticType) (body: DropperBody) =
    match ownedChildren(firstConstructorName(named)(body))(named)(body) with
        | [] -> emitTypeDrop(valueTemp)(named)(body)
        | children ->
            match freshDropperLabel("rc_drop_shared")(body) with
                | (sharedLabel, labelBody) ->
                    labelBody
                    |> emitUniqueTest(valueTemp)(sharedLabel)
                    |> emitAdtFieldDrops(valueTemp)(typeIsTagless(named)(body))(children)
                    |> emitDropper(Label(sharedLabel))
                    |> emitTypeDrop(valueTemp)(named)
and emitAdtFieldDrops (valueTemp: Int) (tagless: Bool) (children: List(HeapLayoutChild)) (body: DropperBody) =
    match children with
        | [] -> body
        | HeapLayoutChild { fieldIndex = index, childType = childType } :: rest ->
            match freshDropperTemp(body) with
                | (childTemp, childBody) ->
                    childBody
                    |> emitDropper(GetAdtField(childTemp)(valueTemp)(index)(tagless))
                    |> emitChildDrop(childTemp)(childType)
                    |> emitAdtFieldDrops(valueTemp)(tagless)(rest)
and emitRecursiveAdtDrop (valueTemp: Int) (named: SemanticType) (body: DropperBody) =
    match synthesizeAdtDropperIn(named)(body) with
        | (label, synthesizedBody) ->
            match freshDropperTemp(synthesizedBody) with
                | (environmentTemp, environmentBody) ->
                    match freshDropperTemp(environmentBody) with
                        | (resultTemp, resultBody) ->
                            resultBody
                            |> emitDropper(LoadConstInt(environmentTemp)(0))
                            |> emitDropper(CallKnown(resultTemp)(label)(environmentTemp)(valueTemp)(-1)(false))
and synthesizeAdtDropperIn (named: SemanticType) (body: DropperBody) =
    match cachedAdtLabel(formatSemanticType(named))(body) with
        | Some(label) -> (label, body)
        | None ->
            match registerAdtLabel(formatSemanticType(named))(body) with
                | (label, registered) ->
                    registered
                    |> beginSynthesizedBody
                    |> emitAdtDropperBody(named)
                    |> finishSynthesizedBody(label)(named
                    |> formatSemanticType
                    |> createAdtDropperOrigin(label))(registered)
                    |> (given (outer) -> (label, outer))
and emitAdtDropperBody (named: SemanticType) (body: DropperBody) =
    match openSynthesizedValue(body) with
        | (valueTemp, valueBody) ->
            match freshDropperLabel("rcdrop_shared")(valueBody) with
                | (sharedLabel, labelBody) ->
                    labelBody
                    |> emitUniqueTest(valueTemp)(sharedLabel)
                    |> emitConstructorSwitch(valueTemp)(named)(sharedLabel)
                    |> emitDropper(Label(sharedLabel))
                    |> emitTypeDrop(valueTemp)(named)
                    |> emitReturnZero
and emitConstructorSwitch (valueTemp: Int) (named: SemanticType) (sharedLabel: Str) (body: DropperBody) =
    match freshDropperTemp(body) with
        | (tagTemp, tagBody) ->
            match tagBody
            |> emitConstructorTagRead(tagTemp)(valueTemp)(named)
            |> allocateConstructorLabels(namedTypeConstructors(named)(body)) with
                | (blocks, blocksBody) ->
                    blocksBody
                    |> emitDropper(SwitchTag(tagTemp)(switchCases(blocks)(0))(sharedLabel))
                    |> emitConstructorBlocks(valueTemp)(named)(blocks)(sharedLabel)
and emitConstructorBlocks (valueTemp: Int) (named: SemanticType) (blocks: List((Str, Str))) (sharedLabel: Str) (body: DropperBody) =
    match blocks with
        | [] -> body
        | (label, constructorName) :: rest ->
            body
            |> emitDropper(Label(label))
            |> emitAdtFieldDrops(valueTemp)(typeIsTagless(named)(body))(ownedChildren(Some(constructorName))(named)(body))
            |> emitDropper(Jump(sharedLabel))
            |> emitConstructorBlocks(valueTemp)(named)(rest)(sharedLabel)

// True when releasing a value of this type reaches past its own allocation: a list owns its spine
// and its elements, a tuple or named type its owned children; a string-like leaf owns one
// allocation and needs no helper.
let structuralReleaseNeedsHelper (semanticType: SemanticType) (environment: TypeEnvironment) =
    match semanticType with
        | SemList(_element) -> true
        | SemTuple(elements) ->
            length(filter(given (element: SemanticType) -> !canArenaResetLayout(element))(elements)) > 0
        | SemNamed(_symbolId, _name, _arguments) ->
            match classifyHeapLayout(semanticType)(environment) with
                | HeapLayoutFacts { containsOwnedChild = containsOwnedChild } -> containsOwnedChild
        | _ -> false

let synthesisResult (label: Maybe(Str)) (body: DropperBody) =
    match body with
        | DropperBody { cache = cache, functions = functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId } -> DropperSynthesis(label = label, cache = cache, functions = functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId)

let emitStructuralBody (semanticType: SemanticType) (body: DropperBody) =
    match openSynthesizedValue(body) with
        | (valueTemp, valueBody) ->
            valueBody
            |> emitChildDrop(valueTemp)(semanticType)
            |> emitReturnZero

let synthesizeStructuralOwnerDropperIn (semanticType: SemanticType) (body: DropperBody) =
    if !structuralReleaseNeedsHelper(semanticType)(bodyEnvironment(body))
    then synthesisResult(None)(body)
    else
        match cachedStructuralLabel(formatSemanticType(semanticType))(body) with
            | Some(label) -> synthesisResult(Some(label))(body)
            | None ->
                match registerStructuralLabel(formatSemanticType(semanticType))(body) with
                    | (label, registered) ->
                        registered
                        |> beginSynthesizedBody
                        |> emitStructuralBody(semanticType)
                        |> finishSynthesizedBody(label)(semanticType
                        |> formatSemanticType
                        |> createStructuralOwnerDropperOrigin(label))(registered)
                        |> synthesisResult(Some(label))

// Names the whole-value release of `semanticType` as a callable helper, synthesizing it and any
// ADT dropper it calls, or answers `None` when the release is a single allocation and needs no
// helper. `semanticType` is the caller's resolved type; `definitions` are the constructors in
// scope in declaration order (a constructor's tag is its index among its type's constructors);
// the cache and counters are the caller's and come back updated.
let synthesizeStructuralOwnerDropper (semanticType: SemanticType) (definitions: List(ConstructorInferenceDefinition)) (cache: DropperLabelCache) (nextLambdaId: Int) (nextLabelId: Int) =
    match openDropperBody(definitions)(cache)(nextLambdaId)(nextLabelId) with
        | (ids, body) ->
            synthesizeStructuralOwnerDropperIn(renumberType(ids)(semanticType))(body)

// Names the constructor-switching dropper of a named type, synthesizing it once; a type that is
// not named has no such dropper.
let synthesizeRuntimeManagedAdtDropper (semanticType: SemanticType) (definitions: List(ConstructorInferenceDefinition)) (cache: DropperLabelCache) (nextLambdaId: Int) (nextLabelId: Int) =
    match openDropperBody(definitions)(cache)(nextLambdaId)(nextLabelId) with
        | (ids, body) ->
            match renumberType(ids)(semanticType) with
                | SemNamed(_symbolId, _name, _arguments) as named ->
                    match synthesizeAdtDropperIn(named)(body) with
                        | (label, synthesized) -> synthesisResult(Some(label))(synthesized)
                | _ -> synthesisResult(None)(body)

// What the lowering knows about an owned aggregate when its scope releases it: whether the value
// is provably the only reference to its whole graph (a fresh list literal or cons chain, a fresh
// constructor tree of a recursive-copy type), and the constructor that built it when the value is
// a direct constructor application.
type OwnedReleasePlan =
    | deepUnique: Bool
    | constructorName: Maybe(Str)

// The inline release program of one owned aggregate at its scope exit, in emission order, with
// the counters, cache, and dropper functions it advanced.
type InlineReleaseSynthesis =
    | instructions: List(IrInstructionKind)
    | nextTemp: Int
    | nextLocal: Int
    | cache: DropperLabelCache
    | functions: List(IrFunction)
    | nextLambdaId: Int
    | nextLabelId: Int

// The unique-list walk of a `let`-owned list built fresh in its own scope, stage 0's
// `EmitRuntimeManagedUniqueListDrop`: every cell is known unique, so no uniqueness test guards the
// head release and the tail advance.
let emitUniqueListDrop (listTemp: Int) (elementType: SemanticType) (body: DropperBody) =
    match freshDropperLocal(body) with
        | (currentSlot, slotBody) ->
            match slotBody
            |> emitDropper(StoreLocal(currentSlot)(listTemp))
            |> freshDropperLabel("rcdrop_unique_list") with
                | (loopLabel, loopBody) ->
                    match freshDropperLabel("rcdrop_unique_list_end")(loopBody) with
                        | (endLabel, labelBody) ->
                            match labelBody
                            |> emitDropper(Label(loopLabel))
                            |> emitListLoopTest(currentSlot)(endLabel) with
                                | (currentTemp, testedBody) ->
                                    testedBody
                                    |> emitListHeadDrop(currentTemp)(elementType)
                                    |> emitListTailAdvance(currentTemp)(currentSlot)(loopLabel)
                                    |> emitDropper(Label(endLabel))

let emitKnownConstructorFieldDrops (valueTemp: Int) (tagless: Bool) (knownUnique: Bool) (sharedLabel: Str) (children: List(HeapLayoutChild)) (body: DropperBody) =
    if knownUnique
    then emitAdtFieldDrops(valueTemp)(tagless)(children)(body)
    else
        body
        |> emitUniqueTest(valueTemp)(sharedLabel)
        |> emitAdtFieldDrops(valueTemp)(tagless)(children)
        |> emitDropper(Label(sharedLabel))

// The release of a `let`-owned ADT built by a known constructor, stage 0's
// `EmitKnownConstructorRuntimeManagedAdtDrop`: only that constructor's owned children are walked,
// under a uniqueness test unless the value is known unique; the shared label is allocated either
// way.
let emitKnownConstructorDrop (valueTemp: Int) (named: SemanticType) (constructorName: Str) (knownUnique: Bool) (body: DropperBody) =
    match ownedChildren(Some(constructorName))(named)(body) with
        | [] -> emitTypeDrop(valueTemp)(named)(body)
        | children ->
            match freshDropperLabel("rc_drop_known_shared")(body) with
                | (sharedLabel, labelBody) ->
                    labelBody
                    |> emitKnownConstructorFieldDrops(valueTemp)(typeIsTagless(named)(body))(knownUnique)(sharedLabel)(children)
                    |> emitTypeDrop(valueTemp)(named)

let namedTypeOwnsChildren (named: SemanticType) (body: DropperBody) =
    match body
    |> bodyEnvironment
    |> classifyHeapLayout(named) with
        | HeapLayoutFacts { containsOwnedChild = containsOwnedChild } -> containsOwnedChild

// The inline release of an owned runtime-managed aggregate by its resolved type, stage 0's
// `EmitOwnedValueDrop` for a tuple, a list, and an ADT with owned children: a tuple walks its
// owned elements under a uniqueness test, a fresh list walks its spine as unique cells and any
// other list tests each cell, an ADT built by a known constructor walks that constructor's owned
// fields, and any other ADT releases through its type-directed dropper. `None` when the value's
// release is a single allocation, which the caller places as an ordinary owner drop.
let emitOwnedAggregateRelease (valueTemp: Int) (semanticType: SemanticType) (plan: OwnedReleasePlan) (body: DropperBody) =
    match (semanticType, plan) with
        | (SemTuple(elements), _plan) ->
            body
            |> emitTupleDrop(valueTemp)(elements)
            |> Some
        | (SemList(element), OwnedReleasePlan { deepUnique = true }) ->
            body
            |> emitUniqueListDrop(valueTemp)(element)
            |> Some
        | (SemList(element), _plan) ->
            body
            |> emitListDrop(valueTemp)(element)
            |> Some
        | (SemNamed(_symbolId, _name, _arguments), OwnedReleasePlan { deepUnique = deepUnique, constructorName = constructorName }) ->
            if namedTypeOwnsChildren(semanticType)(body)
            then
                match constructorName with
                    | Some(constructor) ->
                        body
                        |> emitKnownConstructorDrop(valueTemp)(semanticType)(constructor)(deepUnique)
                        |> Some
                    | None ->
                        body
                        |> emitAdtDrop(valueTemp)(semanticType)
                        |> Some
            else None
        | _ -> None

let instructionKindOf (instruction: IrInstruction) =
    match instruction with
        | IrInstruction { instruction = kind } -> kind

let inlineReleaseResult (body: DropperBody) =
    match body with
        | DropperBody { reversedInstructions = reversed, nextTemp = nextTemp, nextLocal = nextLocal, cache = cache, functions = functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId } ->
            InlineReleaseSynthesis(
                instructions = reversed
                |> reverse
                |> map(instructionKindOf),
                nextTemp = nextTemp,
                nextLocal = nextLocal,
                cache = cache,
                functions = functions,
                nextLambdaId = nextLambdaId,
                nextLabelId = nextLabelId
            )

// Synthesizes the scope-exit release of the owned aggregate loaded into `valueTemp`, continuing
// the caller's temp, local, label, and lambda counters so the instructions splice into the
// caller's function directly; any ADT dropper the walk calls is synthesized into `functions`.
// `None` when the type's release is a single allocation.
let synthesizeOwnedAggregateRelease (valueTemp: Int) (semanticType: SemanticType) (plan: OwnedReleasePlan) (definitions: List(ConstructorInferenceDefinition)) (cache: DropperLabelCache) (nextTemp: Int) (nextLocal: Int) (nextLambdaId: Int) (nextLabelId: Int) =
    match openDropperBody(definitions)(cache)(nextLambdaId)(nextLabelId) with
        | (ids, opened) ->
            match emitOwnedAggregateRelease(valueTemp)(renumberType(ids)(semanticType))(plan)((opened with nextTemp = nextTemp, nextLocal = nextLocal)) with
                | None -> None
                | Some(released) ->
                    released
                    |> inlineReleaseResult
                    |> Some
