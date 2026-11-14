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
import Ashes.Collection.Map.MapTree
import Ashes.Collection.List.filter
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.FunctionOrigins
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.TypeGraphIndex
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.TaglessAdtLayout
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
export (
    type DropperLabelCache(..),
    type DropperSynthesis(..),
    type DropperBody(..),
    type DropperQueryContext(..),
    value emptyDropperLabelCache,
    value containsType,
    value structuralReleaseNeedsHelper,
    value structuralReleaseNeedsHelperIndexed,
    value ownedChildrenOf,
    value isScalarResultType,
    value synthesizeStructuralOwnerDropper,
    value synthesizeRuntimeManagedAdtDropper,
    value synthesizeGeneralAdtDropper,
    value synthesizeRecordDropper,
    type OwnedReleasePlan(..),
    type InlineReleaseSynthesis(..),
    value synthesizeOwnedAggregateRelease,
    value synthesizeOwnedAggregateReleaseIndexed,
    value synthesizeChildDrop,
    type DropperTypes(..),
    value prepareDropperTypes,
    value dropperQueryContextOf,
    value withDropperQueryContext,
    value openDropperBody,
    value renumberType,
    value renumberPreparedType,
    value freshDropperTemp,
    value freshDropperLocal,
    value freshDropperLabel,
    value emitDropper,
    value bodyEnvironment,
    value dropperAdmissibilityFacts,
    value dropperLayoutFacts,
    value dropperLayoutFactsIndexed,
    value dropperAdmissibilityFactsIndexed,
    value dropperConstructorGroupsIndexed,
    value cacheDropperLayout,
    value openSynthesizedValue,
    value beginSynthesizedBody,
    value finishSynthesizedBody,
    value lookupLabel,
    value namedTypeConstructors,
    value typeIsTagless,
    value emitConstructorTagRead,
    value inlineReleaseResult,
)

// The labels already synthesized, keyed by the pretty-printed type they release or copy.
type DropperLabelCache =
    | structuralLabels: List((Str, Str))
    | adtLabels: List((Str, Str))
    | copierLabels: List((Str, Str))
    | normalizerLabels: List((Str, Str))
    | generalDropperLabels: List((Str, Str))
    | recordDropperLabels: List((Str, Str))
    deriving {Eq, Show}

let emptyDropperLabelCache = DropperLabelCache(structuralLabels = [], adtLabels = [], copierLabels = [], normalizerLabels = [], generalDropperLabels = [], recordDropperLabels = [])

// Only completed type/environment queries belong here. The recursive admissibility
// decision still carries its caller's path and is never cached in this table.
type DropperQueryFacts =
    | dropper: Maybe(HeapDropperFacts)
    | admissibility: Maybe(HeapAdmissibilityFacts)

// Node IDs are meaningful only in this graph lineage. Carry the table and graph
// together, including when a synthesis emits no instructions.
type DropperQueryContext =
    | graphIndex: TypeGraphIndex
    | facts: MapTree(Int, DropperQueryFacts)

// The outcome of one synthesis request: the helper's label (`None` when the release needs no
// helper), the cache and counters to carry forward, and the functions synthesized by the request
// in completion order.
type DropperSynthesis =
    | label: Maybe(Str)
    | cache: DropperLabelCache
    | queryContext: DropperQueryContext
    | functions: List(IrFunction)
    | nextLambdaId: Int
    | nextLabelId: Int

// The build state of one synthesized body together with the facts every nested synthesis shares:
// the label cache, the functions completed so far, the two shared counters, the classification
// environment, the lowering's answer to whether a record joins the ownership contract, and the
// record droppers whose bodies are being built.
type DropperLayoutEntry =
    | semanticType: SemanticType
    | facts: HeapDropperFacts

type DropperBody =
    | reversedInstructions: List(IrInstruction)
    | nextTemp: Int
    | nextLocal: Int
    | cache: DropperLabelCache
    | functions: List(IrFunction)
    | nextLambdaId: Int
    | nextLabelId: Int
    | environment: TypeEnvironment
    | graphIndex: TypeGraphIndex
    | sharedFacts: MapTree(Int, DropperQueryFacts)
    | constructorsByType: MapTree(Str, List(ConstructorInferenceDefinition))
    | taglessConstructorIndex: TaglessConstructorIndex
    // Local to this immutable synthesis environment; never persisted with dropper labels.
    | layoutFacts: MapTree(Int, List((SemanticType, HeapLayoutFacts)))
    | dropperFacts: MapTree(Int, List(DropperLayoutEntry))
    | contractRecords: MapTree(Str, Bool)
    | recordsInProgress: List(Str)

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

type PreparedTypeIds =
    | orderedIds: List((Str, Int))
    | byName: MapTree(Str, Int)

// One symbol id per named type declared by a constructor, in first-seen order.
// The lookup map accelerates duplicate checks without determining ID or list order.
let recursive namedTypeIds (definitions: List(ConstructorInferenceDefinition)) (reversedIds: List((Str, Int))) (byName: MapTree(Str, Int)) (nextId: Int) =
    match definitions with
        | [] -> PreparedTypeIds(orderedIds = reverse(reversedIds), byName = byName)
        | ConstructorInferenceDefinition { scheme = TypeScheme { body = body } } :: rest ->
            match schemeResultTypeName(body) with
                | None -> namedTypeIds(rest)(reversedIds)(byName)(nextId)
                | Some(name) ->
                    match Ashes.Collection.Map.getStr(name)(byName) with
                        | Some(_id) -> namedTypeIds(rest)(reversedIds)(byName)(nextId)
                        | None ->
                            namedTypeIds(rest)((name, nextId) :: reversedIds)(Ashes.Collection.Map.setStr(name)(nextId)(byName))(nextId + 1)

let recursive renumberTypeWith (lookup: Str -> Maybe(Int)) (semanticType: SemanticType) =
    match semanticType with
        | SemList(element) ->
            element
            |> renumberTypeWith(lookup)
            |> SemList
        | SemTuple(elements) ->
            elements
            |> map(renumberTypeWith(lookup))
            |> SemTuple
        | SemFunction(parameter, result, row) ->
            SemFunction(renumberTypeWith(lookup)(parameter))(renumberTypeWith(lookup)(result))(row)
        | SemNamed(symbolId, name, arguments) ->
            match lookup(name) with
                | Some(id) ->
                    arguments
                    |> map(renumberTypeWith(lookup))
                    |> SemNamed(id)(name)
                | None ->
                    arguments
                    |> map(renumberTypeWith(lookup))
                    |> SemNamed(symbolId)(name)
        | other -> other

let renumberType (ids: List((Str, Int))) (semanticType: SemanticType) =
    renumberTypeWith(given (name: Str) -> lookupTypeId(name)(ids))(semanticType)

let renumberDefinition (ids: MapTree(Str, Int)) (definition: ConstructorInferenceDefinition) =
    match definition with
        | ConstructorInferenceDefinition { name = name, scheme = TypeScheme { quantified = quantified, body = body, constraints = constraints }, fieldNames = fieldNames } ->
            ConstructorInferenceDefinition(
                name = name,
                scheme = TypeScheme(quantified = quantified, body = renumberTypeWith(given (typeName: Str) -> Ashes.Collection.Map.getStr(typeName)(ids))(body), constraints = constraints),
                fieldNames = fieldNames
            )

let recursive indexDropperConstructors (definitions: List(ConstructorInferenceDefinition)) =
    match definitions with
        | [] -> Ashes.Collection.Map.empty
        | (ConstructorInferenceDefinition { scheme = TypeScheme { body = body } } as definition) :: rest ->
            let index = indexDropperConstructors(rest)
            in
                match schemeResultTypeName(body) with
                    | None -> index
                    | Some(name) ->
                        let previous =
                            match Ashes.Collection.Map.getStr(name)(index) with
                                | Some(bucket) -> bucket
                                | None -> []
                        in Ashes.Collection.Map.setStr(name)(definition :: previous)(index)

// The program's constructors as the release and copy synthesizers read them: one symbol id per
// declared type, every definition renumbered to those ids, and the definitions indexed by the type
// each builds. Preparing this costs a pass over every constructor of the program, so it is built
// where the constructors change and shared by every synthesis until they change again.
// The lowering fills in `contractRecords` where it synthesizes, from its own state: an argument-free
// record of the ownership contract, by name, is released through a dropper of its own.
type DropperTypes =
    | dropperTypeIds: List((Str, Int))
    | dropperTypeIdsByName: MapTree(Str, Int)
    | dropperTypeEnvironment: TypeEnvironment
    | graphIndex: TypeGraphIndex
    | sharedFacts: MapTree(Int, DropperQueryFacts)
    | constructorsByType: MapTree(Str, List(ConstructorInferenceDefinition))
    | taglessConstructorIndex: TaglessConstructorIndex
    | contractRecords: MapTree(Str, Bool)

// Uses the same first-seen IDs as the public list, without scanning it at each named node.
let renumberPreparedType (types: DropperTypes) (semanticType: SemanticType) =
    renumberTypeWith(given (name: Str) -> Ashes.Collection.Map.getStr(name)(types.dropperTypeIdsByName))(semanticType)

let dropperQueryContextOf (types: DropperTypes) = DropperQueryContext(graphIndex = types.graphIndex, facts = types.sharedFacts)

let withDropperQueryContext (context: DropperQueryContext) (types: DropperTypes) = types with graphIndex = context.graphIndex, sharedFacts = context.facts

let bodyQueryContext (body: DropperBody) = DropperQueryContext(graphIndex = body.graphIndex, facts = body.sharedFacts)

let prepareDropperTypes (definitions: List(ConstructorInferenceDefinition)) =
    (let ids = namedTypeIds(definitions)([])(Ashes.Collection.Map.empty)(0)
    in
        let renumbered =
            map(renumberDefinition(ids.byName))(definitions)
        in
            let environment =
                heapExtendConstructorEnvironment(emptyTypeEnvironment(Unit))(renumbered)
            in
                DropperTypes(
                    dropperTypeIds = ids.orderedIds,
                    dropperTypeIdsByName = ids.byName,
                    dropperTypeEnvironment = environment,
                    graphIndex = emptyTypeGraphIndex(environment),
                    sharedFacts = Ashes.Collection.Map.empty,
                    constructorsByType = indexDropperConstructors(renumbered),
                    taglessConstructorIndex = renumbered
                    |> map(given (definition: ConstructorInferenceDefinition) ->
                        match definition with
                            | ConstructorInferenceDefinition { scheme = scheme } -> scheme)
                    |> prepareTaglessConstructorIndex,
                    contractRecords = Ashes.Collection.Map.empty
                ))

let openDropperBody (dropperTypes: DropperTypes) (cache: DropperLabelCache) (nextLambdaId: Int) (nextLabelId: Int) =
    (let ids = dropperTypes.dropperTypeIds
    in
        (ids, DropperBody(
            reversedInstructions = [],
            nextTemp = 0,
            nextLocal = 0,
            cache = cache,
            functions = [],
            nextLambdaId = nextLambdaId,
            nextLabelId = nextLabelId,
            environment = dropperTypes.dropperTypeEnvironment,
            graphIndex = dropperTypes.graphIndex,
            sharedFacts = dropperTypes.sharedFacts,
            constructorsByType = dropperTypes.constructorsByType,
            taglessConstructorIndex = dropperTypes.taglessConstructorIndex,
            layoutFacts = Ashes.Collection.Map.empty,
            dropperFacts = Ashes.Collection.Map.empty,
            contractRecords = dropperTypes.contractRecords,
            recordsInProgress = []
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

// Symbol ids select a bucket only. Full instantiated types must match before reusing facts.
let recursive matchingLayoutFacts (semanticType: SemanticType) (entries: List((SemanticType, HeapLayoutFacts))) =
    match entries with
        | [] -> None
        | (candidate, facts) :: rest ->
            if candidate == semanticType
            then Some(facts)
            else matchingLayoutFacts(semanticType)(rest)

let namedLayoutEntries (symbolId: Int) (body: DropperBody) =
    match Ashes.Collection.Map.getInt(symbolId)(body.layoutFacts) with
        | Some(entries) -> entries
        | None -> []

let recursive matchingDropperFacts (semanticType: SemanticType) (entries: List(DropperLayoutEntry)) =
    match entries with
        | [] -> None
        | entry :: rest ->
            if entry.semanticType == semanticType
            then Some(entry.facts)
            else matchingDropperFacts(semanticType)(rest)

let namedDropperEntries (symbolId: Int) (body: DropperBody) =
    match Ashes.Collection.Map.getInt(symbolId)(body.dropperFacts) with
        | Some(entries) -> entries
        | None -> []

// Pre-existing full facts remain authoritative, including when both caches contain a key.
let cachedDropperLayout (semanticType: SemanticType) (body: DropperBody) =
    match semanticType with
        | SemNamed(symbolId, _name, _arguments) ->
            match body
            |> namedLayoutEntries(symbolId)
            |> matchingLayoutFacts(semanticType) with
                | Some(facts) ->
                    facts
                    |> projectHeapDropperFacts
                    |> Some
                | None ->
                    body
                    |> namedDropperEntries(symbolId)
                    |> matchingDropperFacts(semanticType)
        | _ -> None

let dropperLayoutFacts (semanticType: SemanticType) (body: DropperBody) =
    match cachedDropperLayout(semanticType)(body) with
        | Some(facts) -> facts
        | None -> classifyHeapDropper(semanticType)(body.environment)

let admissibilityChild (child: HeapDropperChild) = (child.childType, child.dropKind)

let dropperAdmissibilityProjection (facts: HeapDropperFacts) =
    HeapAdmissibilityFacts(
        containsResource = facts.containsResource,
        containsUnresolvedType = facts.containsUnresolvedType,
        children = map(admissibilityChild)(facts.children)
    )

let dropperLayoutFactsIndexed (semanticType: SemanticType) (body: DropperBody) =
    match cachedDropperLayout(semanticType)(body) with
        | Some(facts) -> (facts, body)
        | None ->
            match typeGraphNode(semanticType)(body.graphIndex) with
                | (node, graphIndex) ->
                    let queried = body with graphIndex = graphIndex
                    in
                        let previous =
                            match Ashes.Collection.Map.getInt(node.nodeId)(queried.sharedFacts) with
                                | Some(facts) -> facts
                                | None -> DropperQueryFacts(dropper = None, admissibility = None)
                        in
                            match previous.dropper with
                                | Some(facts) -> (facts, queried)
                                | None ->
                                    match classifyHeapDropperIndexed(semanticType)(queried.graphIndex) with
                                        | (facts, discovered) ->
                                            (facts, (queried with graphIndex = discovered, sharedFacts = Ashes.Collection.Map.setInt(node.nodeId)((previous with dropper = Some(facts), admissibility = Some(dropperAdmissibilityProjection(facts))))(queried.sharedFacts)))

// Copier construction uses the source type's groups, without runtime alias erasure.
let dropperConstructorGroupsIndexed (semanticType: SemanticType) (body: DropperBody) =
    match typeGraphNode(semanticType)(body.graphIndex) with
        | (node, graphIndex) -> (node.sourceConstructorGroups, (body with graphIndex = graphIndex))

// Full facts already belong to this immutable synthesis environment. Reuse only their
// context-free projection; admissibility answers still depend on the caller's cycle path.
let dropperAdmissibilityFacts (semanticType: SemanticType) (body: DropperBody) =
    match cachedDropperLayout(semanticType)(body) with
        | Some(facts) ->
            HeapAdmissibilityFacts(
                containsResource = facts.containsResource,
                containsUnresolvedType = facts.containsUnresolvedType,
                children = map(admissibilityChild)(facts.children)
            )
        | None -> classifyHeapAdmissibility(semanticType)(body.environment)

let dropperAdmissibilityFactsIndexed (semanticType: SemanticType) (body: DropperBody) =
    match cachedDropperLayout(semanticType)(body) with
        | Some(facts) -> (dropperAdmissibilityProjection(facts), body)
        | None ->
            match typeGraphNode(semanticType)(body.graphIndex) with
                | (node, graphIndex) ->
                    let queried = body with graphIndex = graphIndex
                    in
                        let previous =
                            match Ashes.Collection.Map.getInt(node.nodeId)(queried.sharedFacts) with
                                | Some(facts) -> facts
                                | None -> DropperQueryFacts(dropper = None, admissibility = None)
                        in
                            // A completed full view stores its projection here too; a
                            // warm query neither walks children nor reallocates that list.
                            match previous.admissibility with
                                | Some(facts) -> (facts, queried)
                                | None ->
                                    match classifyHeapAdmissibilityIndexed(semanticType)(queried.graphIndex) with
                                        | (facts, discovered) -> (facts, (queried with graphIndex = discovered, sharedFacts = Ashes.Collection.Map.setInt(node.nodeId)((previous with admissibility = Some(facts)))(queried.sharedFacts)))

let cacheDropperLayout (semanticType: SemanticType) (body: DropperBody) =
    match semanticType with
        | SemNamed(symbolId, _name, _arguments) ->
            let entries = namedDropperEntries(symbolId)(body)
            in
                match cachedDropperLayout(semanticType)(body) with
                    | Some(_facts) -> body
                    | None ->
                        match dropperLayoutFactsIndexed(semanticType)(body) with
                            | (facts, queriedBody) -> queriedBody with dropperFacts = Ashes.Collection.Map.setInt(symbolId)(DropperLayoutEntry(semanticType = semanticType, facts = facts) :: entries)(queriedBody.dropperFacts)
        | _ -> body

// The owned type name a whole-value `RcDrop` releases under. The backend releases a `Function`
// through the closure protocol (its dropper at the closure's fourth word), so a user type of that
// name is tagged apart from it, as stage 0's `RuntimeManagedAdtTypeName` does.
let dropperTypeName (semanticType: SemanticType) =
    match semanticType with
        | SemString -> "String"
        | SemBytes -> "Bytes"
        | SemBigInt -> "BigInt"
        | SemList(_element) -> "List"
        | SemTuple(_elements) -> "Tuple"
        | SemNamed(_symbolId, name, _arguments) ->
            if name == "Function"
            then "Function_"
            else name
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
            in outer with cache = cache, graphIndex = inner.graphIndex, sharedFacts = inner.sharedFacts, layoutFacts = inner.layoutFacts, dropperFacts = inner.dropperFacts, functions = append(functions)([function]), nextLambdaId = nextLambdaId, nextLabelId = nextLabelId

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
        | DropperBody { cache = DropperLabelCache { structuralLabels = structural, adtLabels = adt, copierLabels = copiers }, nextLambdaId = nextLambdaId } ->
            let label = "__rcdrop_structural_" + Ashes.Text.fromInt(nextLambdaId)
            in (label, (body with cache = (body.cache with structuralLabels = (key, label) :: structural), nextLambdaId = nextLambdaId + 1))

let registerAdtLabel (key: Str) (body: DropperBody) =
    match body with
        | DropperBody { cache = DropperLabelCache { structuralLabels = structural, adtLabels = adt, copierLabels = copiers }, nextLambdaId = nextLambdaId } ->
            let label = "__rcdrop_" + Ashes.Text.fromInt(nextLambdaId)
            in (label, (body with cache = (body.cache with adtLabels = (key, label) :: adt), nextLambdaId = nextLambdaId + 1))

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

let dropperConstructorsOfType (name: Str) (body: DropperBody) =
    match Ashes.Collection.Map.getStr(name)(body.constructorsByType) with
        | Some(definitions) -> definitions
        | None -> []

let namedTypeConstructors (named: SemanticType) (body: DropperBody) =
    match named with
        | SemNamed(_symbolId, name, _arguments) ->
            body
            |> dropperConstructorsOfType(name)
            |> map(given (definition: ConstructorInferenceDefinition) ->
                match definition with
                    | ConstructorInferenceDefinition { name = constructorName } -> constructorName)
        | _ -> []

let firstConstructorName (named: SemanticType) (body: DropperBody) =
    match named with
        | SemNamed(_symbolId, name, _arguments) ->
            match dropperConstructorsOfType(name)(body) with
                | ConstructorInferenceDefinition { name = first } :: _rest -> Some(first)
                | [] -> None
        | _ -> None

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
    match named with
        | SemNamed(_symbolId, name, _arguments) ->
            match dropperConstructorsOfType(name)(body) with
                | ConstructorInferenceDefinition { scheme = scheme } :: [] -> isTaglessAdtConstructorPrepared(isBuiltinResourceTypeName)(body.taglessConstructorIndex)(false)(scheme)
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
    match dropperLayoutFactsIndexed(semanticType)(body) with
        | (facts, queried) ->
            (filter(given (child: HeapDropperChild) -> child.dropKind != NoChildDrop && child.constructorName == constructorName)(facts.children), queried)

// `Result(Str, Int)` and `Result(Str, Float)` carry no owned payload worth walking: a single
// release of the cell is their whole drop.
let isScalarResultType (named: SemanticType) =
    match named with
        | SemNamed(_symbolId, "Result", SemString :: SemInt :: []) -> true
        | SemNamed(_symbolId, "Result", SemString :: SemFloat :: []) -> true
        | _ -> false

// A recursive-copy or owned-child ADT is released by its own constructor-switching dropper.
let usesRecursiveDropper (named: SemanticType) (body: DropperBody) =
    match dropperLayoutFactsIndexed(named)(body) with
        | (HeapDropperFacts { runtimeOwnedChildAdtSupported = true }, queried) -> (true, queried)
        | (_facts, queried) ->
            match heapRuntimeRecursiveCopyAdtLayoutIndexed(named)(queried.graphIndex) with
                | (supported, graphIndex) -> (supported, (queried with graphIndex = graphIndex))

let cachedGeneralDropperLabel (key: Str) (body: DropperBody) =
    match body with
        | DropperBody { cache = DropperLabelCache { generalDropperLabels = labels } } -> lookupLabel(key)(labels)

let registerGeneralDropperLabel (key: Str) (body: DropperBody) =
    match body with
        | DropperBody { cache = DropperLabelCache { generalDropperLabels = labels }, nextLambdaId = nextLambdaId } ->
            let label = "__rcdrop_general_" + Ashes.Text.fromInt(nextLambdaId)
            in (label, (body with cache = (body.cache with generalDropperLabels = (key, label) :: labels), nextLambdaId = nextLambdaId + 1))

let cachedRecordDropperLabel (key: Str) (body: DropperBody) =
    match body with
        | DropperBody { cache = DropperLabelCache { recordDropperLabels = labels } } -> lookupLabel(key)(labels)

let registerRecordDropperLabel (key: Str) (body: DropperBody) =
    match body with
        | DropperBody { cache = DropperLabelCache { recordDropperLabels = labels }, nextLambdaId = nextLambdaId } ->
            let label = "__rcdrop_record_" + Ashes.Text.fromInt(nextLambdaId)
            in (label, (body with cache = (body.cache with recordDropperLabels = (key, label) :: labels), nextLambdaId = nextLambdaId + 1))

let recursive containsKey (key: Str) (keys: List(Str)) =
    match keys with
        | [] -> false
        | candidate :: rest -> candidate == key || containsKey(key)(rest)

// A record the lowering says joins the ownership contract, outside its own dropper's body.
let usesRecordDropper (named: SemanticType) (body: DropperBody) =
    match named with
        | SemNamed(_symbolId, name, []) ->
            Ashes.Collection.Map.getStr(name)(body.contractRecords) == Some(true) && !containsKey(formatSemanticType(named))(body.recordsInProgress)
        | _ -> false

// Whether two named types are the same one for the admissibility walk's recursion check, as their
// rendered text would say: an argument-free type by its name, without rendering a copy of it.
let sameTypeKey (left: SemanticType) (right: SemanticType) =
    match (left, right) with
        | (SemNamed(_leftId, leftName, []), SemNamed(_rightId, rightName, [])) -> leftName == rightName
        | (SemNamed(_leftId, _leftName, []), _) -> false
        | (_, SemNamed(_rightId, _rightName, [])) -> false
        | _ -> formatSemanticType(left) == formatSemanticType(right)

let recursive containsType (semanticType: SemanticType) (path: List(SemanticType)) =
    match path with
        | [] -> false
        | candidate :: rest -> sameTypeKey(candidate)(semanticType) || containsType(semanticType)(rest)

// Stage 0's `IsGeneralRcAdmissible` over the synthesis environment: a resolved graph of inline
// values, strings, byte buffers, big integers, lists, tuples and algebraic data types. A named
// type already on the walk's path is taken as admissible, which lets a recursive type through.
let recursive originalGeneralRcAdmissible (semanticType: SemanticType) (path: List(SemanticType)) (body: DropperBody) =
    match semanticType with
        | SemString -> (true, body)
        | SemBytes -> (true, body)
        | SemBigInt -> (true, body)
        | SemList(element) -> originalGeneralRcAdmissible(element)(path)(body)
        | SemTuple(elements) -> originalAllGeneralRcAdmissible(elements)(path)(body)
        | SemNamed(_symbolId, name, _arguments) ->
            if containsType(semanticType)(path)
            then (true, body)
            else
                match dropperAdmissibilityFactsIndexed(semanticType)(body) with
                    | (facts, queried) ->
                        match (dropperConstructorsOfType(name)(queried), facts) with
                            | ([], _facts) -> (false, queried)
                            | (_constructors, HeapAdmissibilityFacts { containsResource = true }) -> (false, queried)
                            | (_constructors, HeapAdmissibilityFacts { containsUnresolvedType = true }) -> (false, queried)
                            | (_constructors, HeapAdmissibilityFacts { children = children }) -> originalAllChildrenGeneralRcAdmissible(children)(semanticType :: path)(queried)
        | other -> (canArenaResetLayout(other), body)
and originalAllGeneralRcAdmissible (types: List(SemanticType)) (path: List(SemanticType)) (body: DropperBody) =
    match types with
        | [] -> (true, body)
        | semanticType :: rest ->
            match originalGeneralRcAdmissible(semanticType)(path)(body) with
                | (false, queried) -> (false, queried)
                | (true, queried) -> originalAllGeneralRcAdmissible(rest)(path)(queried)
and originalAllChildrenGeneralRcAdmissible (children: List((SemanticType, HeapChildDropKind))) (path: List(SemanticType)) (body: DropperBody) =
    match children with
        | [] -> (true, body)
        | (_childType, DropClosure) :: _rest -> (false, body)
        | (_childType, UnsupportedChildDrop) :: _rest -> (false, body)
        | (childType, _dropKind) :: rest ->
            match originalGeneralRcAdmissible(childType)(path)(body) with
                | (false, queried) -> (false, queried)
                | (true, queried) -> originalAllChildrenGeneralRcAdmissible(rest)(path)(queried)

// The pending work covers all expanded children, including when another branch
// reaches an exact type already visited. A rendered recursion key can alias distinct
// semantic types; that case must retain the original path-only interpretation.
type DropperAdmissibilityWork =
    | VisitDropperAdmissibilityType(SemanticType)
    | CheckDropperAdmissibilityChild(SemanticType, HeapChildDropKind)

let recursive prependDropperAdmissibilityTypes types pending =
    match types with
        | [] -> pending
        | semanticType :: rest -> VisitDropperAdmissibilityType(semanticType) :: prependDropperAdmissibilityTypes(rest)(pending)

let recursive prependDropperAdmissibilityChildren children pending =
    match children with
        | [] -> pending
        | (semanticType, dropKind) :: rest -> CheckDropperAdmissibilityChild(semanticType)(dropKind) :: prependDropperAdmissibilityChildren(rest)(pending)

// The cycle relation has two disjoint key classes: bare named types compare
// names, while every other path entry compares its rendered text. Keep them
// separate even when their strings match, and retain the first initial-path
// entry so alias fallback has exactly the same priority as the linear search.
type DropperAdmissibilityKey =
    | BareDropperAdmissibilityKey(Str)
    | RenderedDropperAdmissibilityKey(Str)

type DropperAdmissibilityVisited =
    | bare: List(SemanticType)
    | rendered: List((Str, SemanticType))

let dropperAdmissibilityKey semanticType =
    match semanticType with
        | SemNamed(_id, name, []) -> BareDropperAdmissibilityKey(name)
        | _ ->
            semanticType
            |> formatSemanticType
            |> RenderedDropperAdmissibilityKey

let recursive bareDropperAdmissibilityVisitedType name remaining =
    match remaining with
        | [] -> None
        | (SemNamed(_id, previousName, []) as previous) :: rest ->
            if previousName == name
            then Some(previous)
            else bareDropperAdmissibilityVisitedType(name)(rest)
        | _ :: rest -> bareDropperAdmissibilityVisitedType(name)(rest)

let recursive renderedDropperAdmissibilityVisitedType text remaining =
    match remaining with
        | [] -> None
        | (previousKey, previous) :: rest ->
            if previousKey == text
            then Some(previous)
            else renderedDropperAdmissibilityVisitedType(text)(rest)

let dropperAdmissibilityVisitedType key (visited: DropperAdmissibilityVisited) =
    match key with
        | BareDropperAdmissibilityKey(name) -> bareDropperAdmissibilityVisitedType(name)(visited.bare)
        | RenderedDropperAdmissibilityKey(text) -> renderedDropperAdmissibilityVisitedType(text)(visited.rendered)

let withDropperAdmissibilityVisit key semanticType (visited: DropperAdmissibilityVisited) =
    match key with
        | BareDropperAdmissibilityKey(_name) -> visited with bare = semanticType :: visited.bare
        | RenderedDropperAdmissibilityKey(text) -> visited with rendered = (text, semanticType) :: visited.rendered

let recursive prependInitialDropperAdmissibilityVisits path visited =
    match path with
        | [] -> visited
        | semanticType :: rest ->
            visited
            |> withDropperAdmissibilityVisit(dropperAdmissibilityKey(semanticType))(semanticType)
            |> prependInitialDropperAdmissibilityVisits(rest)

let initialDropperAdmissibilityVisited path =
    // Prepending the reversed path preserves its first-match priority without
    // repeated membership searches or removing duplicate nominal aliases.
    prependInitialDropperAdmissibilityVisits(reverse(path))(DropperAdmissibilityVisited(bare = [], rendered = []))

let dropperAdmissibilityVisitStatus key semanticType visited =
    match dropperAdmissibilityVisitedType(key)(visited) with
        | None -> Some(false)
        | Some(previous) ->
            if previous == semanticType
            then Some(true)
            else None

let recursive searchDropperAdmissibility pending visited body =
    match pending with
        | [] -> (Some(true), body)
        | CheckDropperAdmissibilityChild(_semanticType, DropClosure) :: _rest -> (Some(false), body)
        | CheckDropperAdmissibilityChild(_semanticType, UnsupportedChildDrop) :: _rest -> (Some(false), body)
        | CheckDropperAdmissibilityChild(semanticType, _dropKind) :: rest -> searchDropperAdmissibility(VisitDropperAdmissibilityType(semanticType) :: rest)(visited)(body)
        | VisitDropperAdmissibilityType(semanticType) :: rest ->
            match semanticType with
                | SemString -> searchDropperAdmissibility(rest)(visited)(body)
                | SemBytes -> searchDropperAdmissibility(rest)(visited)(body)
                | SemBigInt -> searchDropperAdmissibility(rest)(visited)(body)
                | SemList(element) -> searchDropperAdmissibility(VisitDropperAdmissibilityType(element) :: rest)(visited)(body)
                | SemTuple(elements) ->
                    searchDropperAdmissibility(prependDropperAdmissibilityTypes(elements)(rest))(visited)(body)
                | SemNamed(_symbolId, name, _arguments) ->
                    // Match the dropper's cycle check before inspecting constructors
                    // or resource facts; its contract differs from Core's predicate.
                    let key = dropperAdmissibilityKey(semanticType)
                    in
                        match dropperAdmissibilityVisitStatus(key)(semanticType)(visited) with
                            | None -> (None, body)
                            | Some(true) -> searchDropperAdmissibility(rest)(visited)(body)
                            | Some(false) ->
                                match dropperAdmissibilityFactsIndexed(semanticType)(body) with
                                    | (facts, queried) ->
                                        match (dropperConstructorsOfType(name)(queried), facts) with
                                            | ([], _facts) -> (Some(false), queried)
                                            | (_constructors, HeapAdmissibilityFacts { containsResource = true }) -> (Some(false), queried)
                                            | (_constructors, HeapAdmissibilityFacts { containsUnresolvedType = true }) -> (Some(false), queried)
                                            | (_constructors, HeapAdmissibilityFacts { children = children }) ->
                                                searchDropperAdmissibility(prependDropperAdmissibilityChildren(children)(rest))(withDropperAdmissibilityVisit(key)(semanticType)(visited))(queried)
                | other ->
                    if canArenaResetLayout(other)
                    then searchDropperAdmissibility(rest)(visited)(body)
                    else (Some(false), body)

let generalRcAdmissible (semanticType: SemanticType) (path: List(SemanticType)) (body: DropperBody) =
    (let visited = initialDropperAdmissibilityVisited(path)
    in
        match searchDropperAdmissibility([VisitDropperAdmissibilityType(semanticType)])(visited)(body) with
            | (Some(answer), queried) -> (answer, queried)
            | (None, queried) -> originalGeneralRcAdmissible(semanticType)(path)(queried))

// Stage 0's `NeedsRuntimeManagedAdtNormalizer`: an admissible named type that neither the fixed
// copy-out nor the inline runtime-managed layouts express. Its values are released through a
// dropper of their own, synthesized once per type.
let usesGeneralDropper (named: SemanticType) (body: DropperBody) =
    match dropperLayoutFactsIndexed(named)(body) with
        | (HeapDropperFacts { shallowCopy = true }, queried) -> (false, queried)
        | (HeapDropperFacts { runtimeRecordAdtSupported = true }, queried) -> (false, queried)
        | (HeapDropperFacts { runtimeOwnedChildAdtSupported = true }, queried) -> (false, queried)
        | (HeapDropperFacts { runtimeTcoOwnedChildAdtSupported = true }, queried) -> (false, queried)
        | (HeapDropperFacts { runtimePositionalAdtSupported = true }, queried) -> (false, queried)
        | (_facts, queried) -> generalRcAdmissible(named)([])(queried)

let recursive allocateConstructorLabels (labelName: Str) (names: List(Str)) (body: DropperBody) =
    match names with
        | [] -> ([], body)
        | name :: rest ->
            match freshDropperLabel(labelName)(body) with
                | (label, labelBody) ->
                    match allocateConstructorLabels(labelName)(rest)(labelBody) with
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
        // A closure is a self-contained owned child: its own `RcDrop` handles both the
        // reference-counted and arena forms (the backend's `emitRuntimeRcClosureDrop`/
        // `CleanupResource("Function")`), so the structural dropper just releases it directly,
        // exactly like a string, bytes, or BigInt leaf.
        | SemFunction(_argument, _result, _capabilityRow) -> emitRcDrop(valueTemp)("Function")(body)
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
                | ([], queried) -> emitRcDrop(valueTemp)("Tuple")(queried)
                | (children, queried) ->
                    queried
                    |> emitUniqueTest(valueTemp)(sharedLabel)
                    |> emitTupleChildDrops(valueTemp)(children)
                    |> emitDropper(Label(sharedLabel))
                    |> emitRcDrop(valueTemp)("Tuple")
and emitTupleChildDrops (valueTemp: Int) (children: List(HeapDropperChild)) (body: DropperBody) =
    match children with
        | [] -> body
        | HeapDropperChild { fieldIndex = index, childType = childType } :: rest ->
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
        body
        |> cacheDropperLayout(named)
        |> emitClassifiedAdtDrop(valueTemp)(named)
and emitClassifiedAdtDrop (valueTemp: Int) (named: SemanticType) (body: DropperBody) =
    match usesRecursiveDropper(named)(body) with
        | (true, queried) -> emitRecursiveAdtDrop(valueTemp)(named)(queried)
        | (false, queried) ->
            match usesGeneralDropper(named)(queried) with
                | (true, general) -> emitGeneralAdtDrop(valueTemp)(named)(general)
                | (false, general) ->
                    if usesRecordDropper(named)(general)
                    then emitRecordDrop(valueTemp)(named)(general)
                    else emitFirstConstructorDrop(valueTemp)(named)(general)
// Stage 0's `TryEmitContractRecordDropCall`: a record of the ownership contract is released by one
// call to a dropper synthesized once per type, whose body is the release written inline.
and emitRecordDrop (valueTemp: Int) (named: SemanticType) (body: DropperBody) =
    match synthesizeRecordDropperIn(named)(body) with
        | (label, synthesizedBody) ->
            match freshDropperTemp(synthesizedBody) with
                | (environmentTemp, environmentBody) ->
                    match freshDropperTemp(environmentBody) with
                        | (resultTemp, resultBody) ->
                            resultBody
                            |> emitDropper(LoadConstInt(environmentTemp)(0))
                            |> emitDropper(CallKnown(resultTemp)(label)(environmentTemp)(valueTemp)(-1)(false))
and synthesizeRecordDropperIn (named: SemanticType) (body: DropperBody) =
    (let key = formatSemanticType(named)
    in
        match cachedRecordDropperLabel(key)(body) with
            | Some(label) -> (label, body)
            | None ->
                match registerRecordDropperLabel(key)(body) with
                    | (label, registered) ->
                        match (registered with recordsInProgress = key :: registered.recordsInProgress) |> beginSynthesizedBody |> openSynthesizedValue with
                            | (valueTemp, valueBody) ->
                                valueBody
                                |> emitFirstConstructorDrop(valueTemp)(named)
                                |> emitReturnZero
                                |> finishSynthesizedBody(label)(createAdtDropperOrigin(label)(key))(registered)
                                |> (given (outer) -> (label, outer)))
// Stage 0's `EmitGeneralRuntimeManagedAdtDrop`: on the last reference the dropper releases every
// owned field of the live constructor, then the cell. Its label is registered before its body, so
// a field of the same type calls it.
and emitGeneralAdtDrop (valueTemp: Int) (named: SemanticType) (body: DropperBody) =
    match synthesizeGeneralDropperIn(named)(body) with
        | (label, synthesizedBody) ->
            match freshDropperTemp(synthesizedBody) with
                | (environmentTemp, environmentBody) ->
                    match freshDropperTemp(environmentBody) with
                        | (resultTemp, resultBody) ->
                            resultBody
                            |> emitDropper(LoadConstInt(environmentTemp)(0))
                            |> emitDropper(CallKnown(resultTemp)(label)(environmentTemp)(valueTemp)(-1)(false))
and synthesizeGeneralDropperIn (named: SemanticType) (body: DropperBody) =
    match cachedGeneralDropperLabel(formatSemanticType(named))(body) with
        | Some(label) -> (label, body)
        | None ->
            match registerGeneralDropperLabel(formatSemanticType(named))(body) with
                | (label, registered) ->
                    registered
                    |> beginSynthesizedBody
                    |> emitAdtDropperBody("rcdrop_general_shared")("rcdrop_general_ctor")(named)
                    |> finishSynthesizedBody(label)(named
                    |> formatSemanticType
                    |> createAdtDropperOrigin(label))(registered)
                    |> (given (outer) -> (label, outer))
and emitFirstConstructorDrop (valueTemp: Int) (named: SemanticType) (body: DropperBody) =
    match ownedChildren(firstConstructorName(named)(body))(named)(body) with
        | ([], queried) -> emitTypeDrop(valueTemp)(named)(queried)
        | (children, queried) ->
            match freshDropperLabel("rc_drop_shared")(queried) with
                | (sharedLabel, labelBody) ->
                    labelBody
                    |> emitUniqueTest(valueTemp)(sharedLabel)
                    |> emitAdtFieldDrops(valueTemp)(typeIsTagless(named)(body))(children)
                    |> emitDropper(Label(sharedLabel))
                    |> emitTypeDrop(valueTemp)(named)
and emitAdtFieldDrops (valueTemp: Int) (tagless: Bool) (children: List(HeapDropperChild)) (body: DropperBody) =
    match children with
        | [] -> body
        | HeapDropperChild { fieldIndex = index, childType = childType } :: rest ->
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
                    |> emitAdtDropperBody("rcdrop_shared")("rcdrop_ctor")(named)
                    |> finishSynthesizedBody(label)(named
                    |> formatSemanticType
                    |> createAdtDropperOrigin(label))(registered)
                    |> (given (outer) -> (label, outer))
and emitAdtDropperBody (sharedName: Str) (constructorLabelName: Str) (named: SemanticType) (body: DropperBody) =
    match body
    |> cacheDropperLayout(named)
    |> openSynthesizedValue with
        | (valueTemp, valueBody) ->
            match freshDropperLabel(sharedName)(valueBody) with
                | (sharedLabel, labelBody) ->
                    labelBody
                    |> emitUniqueTest(valueTemp)(sharedLabel)
                    |> emitConstructorSwitch(constructorLabelName)(valueTemp)(named)(sharedLabel)
                    |> emitDropper(Label(sharedLabel))
                    |> emitTypeDrop(valueTemp)(named)
                    |> emitReturnZero
and emitConstructorSwitch (constructorLabelName: Str) (valueTemp: Int) (named: SemanticType) (sharedLabel: Str) (body: DropperBody) =
    match freshDropperTemp(body) with
        | (tagTemp, tagBody) ->
            match tagBody
            |> emitConstructorTagRead(tagTemp)(valueTemp)(named)
            |> allocateConstructorLabels(constructorLabelName)(namedTypeConstructors(named)(body)) with
                | (blocks, blocksBody) ->
                    blocksBody
                    |> emitDropper(SwitchTag(tagTemp)(switchCases(blocks)(0))(sharedLabel))
                    |> emitConstructorBlocks(valueTemp)(named)(blocks)(sharedLabel)
and emitConstructorBlocks (valueTemp: Int) (named: SemanticType) (blocks: List((Str, Str))) (sharedLabel: Str) (body: DropperBody) =
    match blocks with
        | [] -> body
        | (label, constructorName) :: rest ->
            match ownedChildren(Some(constructorName))(named)(body) with
                | (children, queried) ->
                    queried
                    |> emitDropper(Label(label))
                    |> emitAdtFieldDrops(valueTemp)(typeIsTagless(named)(queried))(children)
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

let structuralReleaseNeedsHelperIndexed (semanticType: SemanticType) (index: TypeGraphIndex) =
    match semanticType with
        | SemList(_element) -> (true, index)
        | SemTuple(elements) ->
            (length(filter(given (element: SemanticType) -> !canArenaResetLayout(element))(elements)) > 0, index)
        | SemNamed(_symbolId, _name, _arguments) ->
            match classifyHeapDropperIndexed(semanticType)(index) with
                | (facts, queried) -> (facts.containsOwnedChild, queried)
        | _ -> (false, index)

let structuralReleaseNeedsHelperIn (semanticType: SemanticType) (body: DropperBody) =
    match semanticType with
        | SemNamed(_symbolId, _name, _arguments) ->
            match dropperLayoutFactsIndexed(semanticType)(body) with
                | (facts, queried) -> (facts.containsOwnedChild, queried)
        | _ ->
            match structuralReleaseNeedsHelperIndexed(semanticType)(body.graphIndex) with
                | (needed, graphIndex) -> (needed, (body with graphIndex = graphIndex))

let synthesisResult (label: Maybe(Str)) (body: DropperBody) =
    match body with
        | DropperBody { cache = cache, functions = functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId } -> DropperSynthesis(label = label, cache = cache, queryContext = bodyQueryContext(body), functions = functions, nextLambdaId = nextLambdaId, nextLabelId = nextLabelId)

let emitStructuralBody (semanticType: SemanticType) (body: DropperBody) =
    match openSynthesizedValue(body) with
        | (valueTemp, valueBody) ->
            valueBody
            |> emitChildDrop(valueTemp)(semanticType)
            |> emitReturnZero

let synthesizeStructuralOwnerDropperIn (semanticType: SemanticType) (body: DropperBody) =
    match structuralReleaseNeedsHelperIn(semanticType)(body) with
        | (false, queried) -> synthesisResult(None)(queried)
        | (true, queried) ->
            match cachedStructuralLabel(formatSemanticType(semanticType))(queried) with
                | Some(label) -> synthesisResult(Some(label))(queried)
                | None ->
                    match registerStructuralLabel(formatSemanticType(semanticType))(queried) with
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
let synthesizeStructuralOwnerDropper (semanticType: SemanticType) (dropperTypes: DropperTypes) (cache: DropperLabelCache) (nextLambdaId: Int) (nextLabelId: Int) =
    match openDropperBody(dropperTypes)(cache)(nextLambdaId)(nextLabelId) with
        | (_ids, body) ->
            synthesizeStructuralOwnerDropperIn(renumberPreparedType(dropperTypes)(semanticType))(body)

// Names the constructor-switching dropper of a named type, synthesizing it once; a type that is
// not named has no such dropper.
// The dropper of a record of the ownership contract, synthesized once per type.
let synthesizeRecordDropper (semanticType: SemanticType) (dropperTypes: DropperTypes) (cache: DropperLabelCache) (nextLambdaId: Int) (nextLabelId: Int) =
    match openDropperBody(dropperTypes)(cache)(nextLambdaId)(nextLabelId) with
        | (_ids, body) ->
            match renumberPreparedType(dropperTypes)(semanticType) with
                | SemNamed(_symbolId, _name, _arguments) as named ->
                    match synthesizeRecordDropperIn(named)(body) with
                        | (label, synthesized) -> synthesisResult(Some(label))(synthesized)
                | _ -> synthesisResult(None)(body)

let synthesizeRuntimeManagedAdtDropper (semanticType: SemanticType) (dropperTypes: DropperTypes) (cache: DropperLabelCache) (nextLambdaId: Int) (nextLabelId: Int) =
    match openDropperBody(dropperTypes)(cache)(nextLambdaId)(nextLabelId) with
        | (_ids, body) ->
            match renumberPreparedType(dropperTypes)(semanticType) with
                | SemNamed(_symbolId, _name, _arguments) as named ->
                    match synthesizeAdtDropperIn(named)(body) with
                        | (label, synthesized) -> synthesisResult(Some(label))(synthesized)
                | _ -> synthesisResult(None)(body)

// Names the dropper of a named type only its normalization helper expresses (stage 0's
// `EmitGeneralRuntimeManagedAdtDrop`), synthesizing it once; a type that is not named has none.
let synthesizeGeneralAdtDropper (semanticType: SemanticType) (dropperTypes: DropperTypes) (cache: DropperLabelCache) (nextLambdaId: Int) (nextLabelId: Int) =
    match openDropperBody(dropperTypes)(cache)(nextLambdaId)(nextLabelId) with
        | (_ids, body) ->
            match renumberPreparedType(dropperTypes)(semanticType) with
                | SemNamed(_symbolId, _name, _arguments) as named ->
                    match synthesizeGeneralDropperIn(named)(body) with
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
    | queryContext: DropperQueryContext
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

let emitKnownConstructorFieldDrops (valueTemp: Int) (tagless: Bool) (knownUnique: Bool) (sharedLabel: Str) (children: List(HeapDropperChild)) (body: DropperBody) =
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
        | ([], queried) -> emitTypeDrop(valueTemp)(named)(queried)
        | (children, queried) ->
            match freshDropperLabel("rc_drop_known_shared")(queried) with
                | (sharedLabel, labelBody) ->
                    labelBody
                    |> emitKnownConstructorFieldDrops(valueTemp)(typeIsTagless(named)(body))(knownUnique)(sharedLabel)(children)
                    |> emitTypeDrop(valueTemp)(named)

let namedTypeOwnsChildren (named: SemanticType) (body: DropperBody) =
    match dropperLayoutFactsIndexed(named)(body) with
        | (facts, queried) -> (facts.containsOwnedChild, queried)

// The inline release of an owned runtime-managed aggregate by its resolved type, stage 0's
// `EmitOwnedValueDrop` for a tuple, a list, and an ADT with owned children: a tuple walks its
// owned elements under a uniqueness test, a fresh list walks its spine as unique cells and any
// other list tests each cell, an ADT built by a known constructor walks that constructor's owned
// fields, and any other ADT releases through its type-directed dropper. `None` when the value's
// release is a single allocation, which the caller places as an ordinary owner drop.
let emitClassifiedOwnedAggregateRelease (valueTemp: Int) (semanticType: SemanticType) (plan: OwnedReleasePlan) (body: DropperBody) =
    match (semanticType, plan) with
        | (SemTuple(elements), _plan) -> (true, emitTupleDrop(valueTemp)(elements)(body))
        | (SemList(element), OwnedReleasePlan { deepUnique = true }) -> (true, emitUniqueListDrop(valueTemp)(element)(body))
        | (SemList(element), _plan) -> (true, emitListDrop(valueTemp)(element)(body))
        | (SemNamed(_symbolId, _name, _arguments), OwnedReleasePlan { deepUnique = deepUnique, constructorName = constructorName }) ->
            match namedTypeOwnsChildren(semanticType)(body) with
                | (true, queried) ->
                    match constructorName with
                        | Some(constructor) -> (true, emitKnownConstructorDrop(valueTemp)(semanticType)(constructor)(deepUnique)(queried))
                        | None -> (true, emitAdtDrop(valueTemp)(semanticType)(queried))
                | (false, queried) -> (false, queried)
        | _ -> (false, body)

let emitOwnedAggregateRelease (valueTemp: Int) (semanticType: SemanticType) (plan: OwnedReleasePlan) (body: DropperBody) =
    body
    |> cacheDropperLayout(semanticType)
    |> emitClassifiedOwnedAggregateRelease(valueTemp)(semanticType)(plan)

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
                queryContext = bodyQueryContext(body),
                functions = functions,
                nextLambdaId = nextLambdaId,
                nextLabelId = nextLabelId
            )

// Synthesizes the scope-exit release of the owned aggregate loaded into `valueTemp`, continuing
// the caller's temp, local, label, and lambda counters so the instructions splice into the
// caller's function directly; any ADT dropper the walk calls is synthesized into `functions`.
// `None` when the type's release is a single allocation.
let synthesizeOwnedAggregateReleaseIndexed (valueTemp: Int) (semanticType: SemanticType) (plan: OwnedReleasePlan) (dropperTypes: DropperTypes) (cache: DropperLabelCache) (nextTemp: Int) (nextLocal: Int) (nextLambdaId: Int) (nextLabelId: Int) =
    match openDropperBody(dropperTypes)(cache)(nextLambdaId)(nextLabelId) with
        | (_ids, opened) ->
            match emitOwnedAggregateRelease(valueTemp)(renumberPreparedType(dropperTypes)(semanticType))(plan)((opened with nextTemp = nextTemp, nextLocal = nextLocal)) with
                | (false, queried) -> (None, bodyQueryContext(queried))
                | (true, released) ->
                    (released
                    |> inlineReleaseResult
                    |> Some, bodyQueryContext(released))

// Compatibility entry point for callers that do not retain a synthesis context.
let synthesizeOwnedAggregateRelease (valueTemp: Int) (semanticType: SemanticType) (plan: OwnedReleasePlan) (dropperTypes: DropperTypes) (cache: DropperLabelCache) (nextTemp: Int) (nextLocal: Int) (nextLambdaId: Int) (nextLabelId: Int) =
    match synthesizeOwnedAggregateReleaseIndexed(valueTemp)(semanticType)(plan)(dropperTypes)(cache)(nextTemp)(nextLocal)(nextLambdaId)(nextLabelId) with
        | (synthesis, _queryContext) -> synthesis

// The inline release of an owned value by its type alone, stage 0's `EmitRuntimeManagedChildDrop`:
// a tuple or a list is walked, a named type released by the dropper its layout calls for, and a
// string-like leaf released as one allocation.
let synthesizeChildDrop (valueTemp: Int) (semanticType: SemanticType) (dropperTypes: DropperTypes) (cache: DropperLabelCache) (nextTemp: Int) (nextLocal: Int) (nextLambdaId: Int) (nextLabelId: Int) =
    match openDropperBody(dropperTypes)(cache)(nextLambdaId)(nextLabelId) with
        | (_ids, opened) ->
            (opened with nextTemp = nextTemp, nextLocal = nextLocal)
            |> emitChildDrop(valueTemp)(renumberPreparedType(dropperTypes)(semanticType))
            |> inlineReleaseResult
