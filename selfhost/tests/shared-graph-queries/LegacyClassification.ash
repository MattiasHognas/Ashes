// Classifies a resolved SemanticType's heap layout for ownership, Perceus drop-insertion, and
// reuse-specialization purposes: whether the value transitively contains a declared resource type
// or an unresolved type variable, how the whole graph can be copied (inline word copy, shallow cell
// copy, deep structural copy, or not at all), whether every owned child can be dropped, whether
// the runtime can reuse the outer cell in place, and — for list, tuple, and named-ADT shapes — the
// per-child type, drop operation kind, and copy kind the drop and reuse machinery will need. A
// zero-cost newtype is erased to its runtime representation before classification, at every
// level, not only at the top.
//
// Invariants:
// - A scalar type (Int, UInt, Float, Rune, Bool) never needs a drop: it has no heap payload.
// - Recursive ADT cycles are guarded by symbol id; the path back to an already-visited symbol
//   reports neither "resource" nor "unresolved" — only a fact reachable via a fresh, non-cyclic
//   path counts, matching the stage-0 reference classifier's own cycle-guard convention. The copy
//   and reuse rules apply the same guard: a cycle is not deep-copyable, is droppable, and does not
//   qualify a child for the record or owned-child reuse layouts.
// - A named type's constructor field types are instantiated against its own concrete type
//   arguments (positionally, via the constructor's generalized result type), never returned raw
//   with generic type-parameter placeholders still attached.
// - Every conservative outcome is explicit: the rejection flags name why runtime reuse of the outer
//   cell is declined rather than leaving the reason to be inferred from a missing positive fact.

import AshesCompiler.Semantics.Types
import Ashes.Collection.Map.MapTree
import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeGraphSearch.typeGraphAny
import AshesCompiler.Semantics.TypeGraphSearch.typeGraphAnyComplete
import AshesCompiler.Semantics.TypeGraphSearch.TypeGraphClosureResult
import AshesCompiler.Semantics.TypeGraphSearch.typeGraphAnyWithPathCuts
import Ashes.Collection.List.append
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import Ashes.Collection.List.length
import Ashes.Collection.Map.MapTree
import AshesCompiler.Semantics.HeapLayoutClassification
export (
    value legacy_heapRuntimeOwnedFieldLayout,
    value legacy_heapRuntimeOwnedTupleElementLayout,
    value legacy_classifyHeapResource,
    value legacy_classifyHeapArenaDeepCopy,
    value legacy_classifyHeapShallowCopy,
    value legacy_classifyHeapOwnedChildrenDroppable,
    value legacy_heapRuntimeRecursiveCopyAdtLayout,
    value legacy_classifyHeapChildren,
    value legacy_classifyHeapChildTypes,
    value legacy_classifyHeapAdmissibility,
    value legacy_classifyHeapCopyPlan,
    value legacy_classifyHeapLayout,
    value legacy_classifyHeapDropper,
)

type LegacyHeapRuntimeReuseSupport =
    | copyAdt: Bool
    | recordAdt: Bool
    | ownedChildAdt: Bool
    | tcoOwnedChildAdt: Bool
    | recursiveAdt: Bool
    deriving {Eq, Show}

let legacy_canArenaResetLayout (semanticType: SemanticType) =
    match semanticType with
        | SemInt -> true
        | SemUInt(_bits) -> true
        | SemFloat -> true
        | SemRune -> true
        | SemBool -> true
        | SemNever -> true
        | _ -> false

let legacy_resolveLayoutType (semanticType: SemanticType) (environment: TypeEnvironment) =
    environment
    |> inferenceTypeResolutionContext
    |> semanticRuntimeRepresentation(semanticType)

let legacy_heapCanReset (semanticType: SemanticType) (environment: TypeEnvironment) =
    environment
    |> legacy_resolveLayoutType(semanticType)
    |> legacy_canArenaResetLayout

// Built-in handle types whose values own an operating-system resource.
let legacy_heapBuiltinResourceTypeName (name: Str) = name == "Socket" || name == "TlsSocket" || name == "Process" || name == "FileHandle" || name == "JoinHandle"

// Types the compiler provides itself rather than user source declares.
let legacy_heapBuiltinTypeName (name: Str) = name == "Unit" || name == "List" || name == "Maybe" || name == "Result" || name == "Task" || legacy_heapBuiltinResourceTypeName(name)

// Whether a named type is already on a classification walk's path. A type is identified by its
// symbol id and its name together: the lowering's own type layer names every declared type with
// id 0 and tells them apart by name alone, so an id-only guard would take a nested record for a
// cycle back into its parent.
let recursive legacy_heapPathContains (symbolId: Int) (name: Str) (path: List((Int, Str))) =
    match path with
        | [] -> false
        | (candidateId, candidateName) :: rest ->
            if candidateId == symbolId && candidateName == name
            then true
            else legacy_heapPathContains(symbolId)(name)(rest)

let recursive legacy_heapAllTypes predicate (types: List(SemanticType)) =
    match types with
        | [] -> true
        | head :: rest ->
            if predicate(head)
            then legacy_heapAllTypes(predicate)(rest)
            else false

let recursive legacy_heapAnyType predicate (types: List(SemanticType)) =
    match types with
        | [] -> false
        | head :: rest ->
            if predicate(head)
            then true
            else legacy_heapAnyType(predicate)(rest)

let recursive legacy_heapAllGroupedFields predicate (grouped: List((Str, List(SemanticType)))) =
    match grouped with
        | [] -> true
        | (_constructorName, fieldTypes) :: rest ->
            if legacy_heapAllTypes(predicate)(fieldTypes)
            then legacy_heapAllGroupedFields(predicate)(rest)
            else false

let recursive legacy_heapAnyGroupedField predicate (grouped: List((Str, List(SemanticType)))) =
    match grouped with
        | [] -> false
        | (_constructorName, fieldTypes) :: rest ->
            if legacy_heapAnyType(predicate)(fieldTypes)
            then true
            else legacy_heapAnyGroupedField(predicate)(rest)

let recursive legacy_heapSameArity (arity: Int) (grouped: List((Str, List(SemanticType)))) =
    match grouped with
        | [] -> true
        | (_constructorName, fieldTypes) :: rest ->
            if length(fieldTypes) == arity
            then legacy_heapSameArity(arity)(rest)
            else false

let legacy_heapEnvironmentConstructors (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { constructors = constructors } -> constructors

// Peels a constructor's curried scheme body (a1 -> a2 -> ... -> Named(...)) into its field
// types and the final, still type-parameterized, result type.
let recursive legacy_heapConstructorFieldShape (body: SemanticType) (reversedParameters: List(SemanticType)) =
    match body with
        | SemFunction(parameter, result, None) -> legacy_heapConstructorFieldShape(result)(parameter :: reversedParameters)
        | _ -> (reverse(reversedParameters), body)

// Maps each of the type's own declared parameters — ordinary fresh type variables at the point a
// TypeDecl registers its constructors, per ProgramInference.ash's registerTypeParameters, not a
// distinct rigid-parameter shape — as they appear in a constructor's generalized result type, to
// the corresponding concrete argument at this use site.
let recursive legacy_heapParameterMapping (resultArguments: List(SemanticType)) (concreteArguments: List(SemanticType)) =
    match (resultArguments, concreteArguments) with
        | ([], _) -> []
        | (_, []) -> []
        | (SemVariable(variableId) :: restResult, concreteType :: restConcrete) -> (variableId, concreteType) :: legacy_heapParameterMapping(restResult)(restConcrete)
        | (_unmapped :: restResult, _concreteType :: restConcrete) -> legacy_heapParameterMapping(restResult)(restConcrete)

// The type a constructor's curried scheme body finally produces, read without building anything.
let recursive legacy_heapConstructorResultType (body: SemanticType) =
    match body with
        | SemFunction(_parameter, result, None) -> legacy_heapConstructorResultType(result)
        | _ -> body

let legacy_heapConstructorBuilds (symbolId: Int) (typeName: Str) (body: SemanticType) =
    match legacy_heapConstructorResultType(body) with
        | SemNamed(candidateId, candidateName, _arguments) -> candidateId == symbolId && candidateName == typeName
        | _ -> false

// Every constructor of the named type identified by symbolId, in declaration order, paired with
// its own fields substituted for this use site's concrete type arguments. The scan covers every
// constructor the environment knows, so one that builds some other type is rejected on its result
// type alone, before its field list is peeled: each query otherwise allocates in proportion to
// the whole program's constructors rather than to the one type asked about.
let recursive legacy_heapNamedTypeSubstitutedFields (symbolId: Int) (typeName: Str) (arguments: List(SemanticType)) (constructors: List(ConstructorInferenceDefinition)) =
    match constructors with
        | [] -> []
        | ConstructorInferenceDefinition { name = constructorName, scheme = TypeScheme { body = body } } :: rest ->
            if legacy_heapConstructorBuilds(symbolId)(typeName)(body)
            then
                match legacy_heapConstructorFieldShape(body)([]) with
                    | (fieldTypes, SemNamed(_candidateId, _candidateName, resultArguments)) ->
                        let mapping = legacy_heapParameterMapping(resultArguments)(arguments)
                        in
                            let substitutedFields =
                                map(applySubstitution(mapping))(fieldTypes)
                            in (constructorName, substitutedFields) :: legacy_heapNamedTypeSubstitutedFields(symbolId)(typeName)(arguments)(rest)
                    | _ -> legacy_heapNamedTypeSubstitutedFields(symbolId)(typeName)(arguments)(rest)
            else legacy_heapNamedTypeSubstitutedFields(symbolId)(typeName)(arguments)(rest)

type LegacyHeapGroupKey =
    | symbolId: Int
    | typeName: Str

type LegacyHeapGroupIndex =
    | groups: MapTree(Int, MapTree(Str, ConstructorFieldGroup))
    | reversedOrder: List(LegacyHeapGroupKey)

let legacy_heapGroupsForId (symbolId: Int) (index: LegacyHeapGroupIndex) =
    match Ashes.Collection.Map.getInt(symbolId)(index.groups) with
        | Some(groups) -> groups
        | None -> Ashes.Collection.Map.empty

let legacy_heapIndexGroup (symbolId: Int) (typeName: Str) (constructorName: Str) (fieldTypes: List(SemanticType)) (resultArguments: List(SemanticType)) (index: LegacyHeapGroupIndex) =
    (let byName = legacy_heapGroupsForId(symbolId)(index)
    in
        match Ashes.Collection.Map.getStr(typeName)(byName) with
            | None ->
                let group = ConstructorFieldGroup(groupSymbolId = symbolId, groupTypeName = typeName, groupIsGeneric = length(resultArguments) > 0, groupFields = [(constructorName, fieldTypes)], groupGenericFields = [(constructorName, fieldTypes, resultArguments)])
                in
                    index with groups = Ashes.Collection.Map.setInt(symbolId)(Ashes.Collection.Map.setStr(typeName)(group)(byName))(index.groups), reversedOrder = LegacyHeapGroupKey(symbolId = symbolId, typeName = typeName) :: index.reversedOrder
            | Some(group) ->
                let updated = group with groupIsGeneric = group.groupIsGeneric || length(resultArguments) > 0, groupFields = (constructorName, fieldTypes) :: group.groupFields, groupGenericFields = (constructorName, fieldTypes, resultArguments) :: group.groupGenericFields
                in
                    index with groups = Ashes.Collection.Map.setInt(symbolId)(Ashes.Collection.Map.setStr(typeName)(updated)(byName))(index.groups))

let recursive legacy_heapCollectGroups (constructors: List(ConstructorInferenceDefinition)) (index: LegacyHeapGroupIndex) =
    match constructors with
        | [] -> index
        | ConstructorInferenceDefinition { name = constructorName, scheme = TypeScheme { body = body } } :: rest ->
            match legacy_heapConstructorFieldShape(body)([]) with
                | (fieldTypes, SemNamed(symbolId, typeName, resultArguments)) ->
                    index
                    |> legacy_heapIndexGroup(symbolId)(typeName)(constructorName)(fieldTypes)(resultArguments)
                    |> legacy_heapCollectGroups(rest)
                | _ -> legacy_heapCollectGroups(rest)(index)

let legacy_heapGroupInDeclarationOrder (group: ConstructorFieldGroup) =
    match group with
        | ConstructorFieldGroup { groupSymbolId = symbolId, groupTypeName = typeName, groupIsGeneric = isGeneric, groupFields = fields, groupGenericFields = genericFields } ->
            ConstructorFieldGroup(
                groupSymbolId = symbolId,
                groupTypeName = typeName,
                groupIsGeneric = isGeneric,
                groupFields = reverse(fields),
                groupGenericFields = reverse(genericFields)
            )

let recursive legacy_heapGroupsInOrder (keys: List(LegacyHeapGroupKey)) (index: LegacyHeapGroupIndex) =
    match keys with
        | [] -> []
        | LegacyHeapGroupKey { symbolId = symbolId, typeName = typeName } :: rest ->
            match index
            |> legacy_heapGroupsForId(symbolId)
            |> Ashes.Collection.Map.getStr(typeName) with
                | Some(group) -> legacy_heapGroupInDeclarationOrder(group) :: legacy_heapGroupsInOrder(rest)(index)
                | None -> legacy_heapGroupsInOrder(rest)(index)

// Local indexed accumulation avoids rebuilding every preceding group for each constructor.
// The returned list retains first-seen group order and declaration order within each group.
let legacy_heapConstructorFieldGroups (constructors: List(ConstructorInferenceDefinition)) =
    (let index = legacy_heapCollectGroups(constructors)(LegacyHeapGroupIndex(groups = Ashes.Collection.Map.empty, reversedOrder = []))
    in
        legacy_heapGroupsInOrder(reverse(index.reversedOrder))(index))

let recursive legacy_heapFindGroup (symbolId: Int) (typeName: Str) (groups: List(ConstructorFieldGroup)) =
    match groups with
        | [] -> None
        | (ConstructorFieldGroup { groupSymbolId = candidateId, groupTypeName = candidateName } as group) :: rest ->
            if candidateId == symbolId && candidateName == typeName
            then Some(group)
            else legacy_heapFindGroup(symbolId)(typeName)(rest)

// Preserve the first matching group, including when incremental declaration batches repeat a key.
let recursive legacy_heapIndexExistingGroups (groups: List(ConstructorFieldGroup)) (index: HeapConstructorLookupIndex) =
    match groups with
        | [] -> index
        | (ConstructorFieldGroup { groupSymbolId = symbolId, groupTypeName = name } as group) :: rest ->
            let byName =
                match Ashes.Collection.Map.getInt(symbolId)(index.groups) with
                    | Some(found) -> found
                    | None -> Ashes.Collection.Map.empty
            in
                let next =
                    match Ashes.Collection.Map.getStr(name)(byName) with
                        | Some(_) -> index
                        | None ->
                            index with groups = Ashes.Collection.Map.setInt(symbolId)(Ashes.Collection.Map.setStr(name)(group)(byName))(index.groups)
                in legacy_heapIndexExistingGroups(rest)(next)

let recursive legacy_heapIndexRecordKinds (constructors: List(ConstructorInferenceDefinition)) (index: HeapConstructorLookupIndex) =
    match constructors with
        | [] -> index
        | ConstructorInferenceDefinition { scheme = TypeScheme { body = body }, fieldNames = fieldNames } :: rest ->
            match legacy_heapConstructorResultType(body) with
                | SemNamed(symbolId, name, _) ->
                    let byName =
                        match Ashes.Collection.Map.getInt(symbolId)(index.records) with
                            | Some(found) -> found
                            | None -> Ashes.Collection.Map.empty
                    in
                        let next =
                            match Ashes.Collection.Map.getStr(name)(byName) with
                                | Some(_) -> index
                                | None ->
                                    index with records = Ashes.Collection.Map.setInt(symbolId)(Ashes.Collection.Map.setStr(name)(length(fieldNames) >= 1)(byName))(index.records)
                        in legacy_heapIndexRecordKinds(rest)(next)
                | _ -> legacy_heapIndexRecordKinds(rest)(index)

// Production environments retain the lookup maps alongside the ordered lists. Extending an
// already prepared environment indexes only the new declaration batch.
let legacy_heapExtendConstructorEnvironment (environment: TypeEnvironment) (definitions: List(ConstructorInferenceDefinition)) =
    (let previous =
        match environment.heapConstructorIndex with
            | Some(index) -> index
            | None ->
                HeapConstructorLookupIndex(groups = Ashes.Collection.Map.empty, records = Ashes.Collection.Map.empty)
                |> legacy_heapIndexExistingGroups(environment.constructorFieldGroups)
                |> legacy_heapIndexRecordKinds(environment.constructors)
    in
        let groups = legacy_heapConstructorFieldGroups(definitions)
        in
            let index =
                previous
                |> legacy_heapIndexExistingGroups(groups)
                |> legacy_heapIndexRecordKinds(definitions)
            in environment with constructors = append(environment.constructors)(definitions), constructorFieldGroups = append(environment.constructorFieldGroups)(groups), heapConstructorIndex = Some(index))

let legacy_heapEnvironmentGroup (symbolId: Int) (name: Str) (environment: TypeEnvironment) =
    match environment.heapConstructorIndex with
        | None -> legacy_heapFindGroup(symbolId)(name)(environment.constructorFieldGroups)
        | Some(HeapConstructorLookupIndex { groups = groups }) ->
            match Ashes.Collection.Map.getInt(symbolId)(groups) with
                | None -> None
                | Some(byName) -> Ashes.Collection.Map.getStr(name)(byName)

let recursive legacy_heapSubstituteGroupFields (arguments: List(SemanticType)) (genericFields: List((Str, List(SemanticType), List(SemanticType)))) =
    match genericFields with
        | [] -> []
        | (constructorName, fieldTypes, resultArguments) :: rest ->
            let mapping = legacy_heapParameterMapping(resultArguments)(arguments)
            in
                (constructorName, map(applySubstitution(mapping))(fieldTypes)) :: legacy_heapSubstituteGroupFields(arguments)(rest)

// The named type's constructors with their fields instantiated at this use site, or an empty list
// for a type whose constructors are not visible in the environment (a compiler-provided handle).
// An environment that indexes its constructors answers from the type's own group, and for a type
// with no parameters the group's field lists are the answer as they stand; one that does not is
// scanned whole.
let legacy_heapNamedTypeFields (symbolId: Int) (name: Str) (arguments: List(SemanticType)) (environment: TypeEnvironment) =
    match environment.constructorFieldGroups with
        | [] ->
            environment
            |> legacy_heapEnvironmentConstructors
            |> legacy_heapNamedTypeSubstitutedFields(symbolId)(name)(arguments)
        | _groups ->
            match legacy_heapEnvironmentGroup(symbolId)(name)(environment) with
                | None -> []
                | Some(ConstructorFieldGroup { groupIsGeneric = false, groupFields = fields }) -> fields
                | Some(ConstructorFieldGroup { groupGenericFields = genericFields }) -> legacy_heapSubstituteGroupFields(arguments)(genericFields)

let legacy_heapNamedTypeConstructors (named: SemanticType) (environment: TypeEnvironment) =
    match named with
        | SemNamed(symbolId, name, arguments) -> legacy_heapNamedTypeFields(symbolId)(name)(arguments)(environment)
        | _ -> []

// Whether the named type's first constructor declares record field names.
let recursive legacy_heapNamedTypeHasFieldNames (symbolId: Int) (typeName: Str) (constructors: List(ConstructorInferenceDefinition)) =
    match constructors with
        | [] -> false
        | ConstructorInferenceDefinition { scheme = TypeScheme { body = body }, fieldNames = fieldNames } :: rest ->
            if legacy_heapConstructorBuilds(symbolId)(typeName)(body)
            then length(fieldNames) >= 1
            else legacy_heapNamedTypeHasFieldNames(symbolId)(typeName)(rest)

let legacy_heapNamedTypeIsRecord (named: SemanticType) (environment: TypeEnvironment) =
    match named with
        | SemNamed(symbolId, name, _arguments) ->
            match environment.heapConstructorIndex with
                | None ->
                    environment
                    |> legacy_heapEnvironmentConstructors
                    |> legacy_heapNamedTypeHasFieldNames(symbolId)(name)
                | Some(HeapConstructorLookupIndex { records = records }) ->
                    match Ashes.Collection.Map.getInt(symbolId)(records) with
                        | None -> false
                        | Some(byName) ->
                            match Ashes.Collection.Map.getStr(name)(byName) with
                                | None -> false
                                | Some(isRecord) -> isRecord
        | _ -> false

let legacy_heapExternalTypeHasDestructor (name: Str) (context: TypeResolutionContext) = externalTypeHasDestructor(name)(context)

let recursive legacy_heapAnyResource (types: List(SemanticType)) (environment: TypeEnvironment) (path: List((Int, Str))) =
    match types with
        | [] -> false
        | head :: rest ->
            if legacy_heapContainsResource(head)(environment)(path)
            then true
            else legacy_heapAnyResource(rest)(environment)(path)
and legacy_heapAnyGroupedResource (grouped: List((Str, List(SemanticType)))) (environment: TypeEnvironment) (path: List((Int, Str))) =
    match grouped with
        | [] -> false
        | (_constructorName, fieldTypes) :: rest ->
            if legacy_heapAnyResource(fieldTypes)(environment)(path)
            then true
            else legacy_heapAnyGroupedResource(rest)(environment)(path)
and legacy_heapContainsResource (semanticType: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) =
    (let resolved = legacy_resolveLayoutType(semanticType)(environment)
    in
        match resolved with
            | SemOpaque(name) ->
                environment
                |> inferenceTypeResolutionContext
                |> legacy_heapExternalTypeHasDestructor(name)
            | SemNamed(symbolId, name, arguments) ->
                if legacy_heapBuiltinResourceTypeName(name)
                then true
                else
                    if legacy_heapPathContains(symbolId)(name)(path)
                    then false
                    else
                        legacy_heapAnyGroupedResource(legacy_heapNamedTypeFields(symbolId)(name)(arguments)(environment))(environment)((symbolId, name) :: path)
            | SemTuple(elements) -> legacy_heapAnyResource(elements)(environment)(path)
            | SemList(element) -> legacy_heapContainsResource(element)(environment)(path)
            | _ -> false)

let recursive legacy_heapFlattenFieldGroups grouped =
    match grouped with
        | [] -> []
        | (_name, fields) :: rest ->
            rest
            |> legacy_heapFlattenFieldGroups
            |> append(fields)

let legacy_heapGraphChildren includeArguments environment semanticType =
    match semanticType with
        | SemNamed(symbolId, name, arguments) ->
            let fields =
                environment
                |> legacy_heapNamedTypeFields(symbolId)(name)(arguments)
                |> legacy_heapFlattenFieldGroups
            in
                if includeArguments
                then append(arguments)(fields)
                else fields
        | SemList(element) -> [element]
        | SemTuple(elements) -> elements
        | _ -> []

let legacy_heapDirectResource environment semanticType =
    match semanticType with
        | SemOpaque(name) ->
            environment
            |> inferenceTypeResolutionContext
            |> legacy_heapExternalTypeHasDestructor(name)
        | SemNamed(_symbolId, name, _arguments) -> legacy_heapBuiltinResourceTypeName(name)
        | _ -> false

let legacy_heapResourceBearing (semanticType: SemanticType) (environment: TypeEnvironment) =
    match typeGraphAny(given (value) -> legacy_resolveLayoutType(value)(environment))(legacy_heapDirectResource(environment))(legacy_heapGraphChildren(false)(environment))(semanticType) with
        | Some(result) -> result
        | None ->
            typeGraphAnyWithPathCuts(given (value) -> legacy_resolveLayoutType(value)(environment))(legacy_heapDirectResource(environment))(legacy_heapGraphChildren(false)(environment))(semanticType)

// Resource-only consumers must not construct copy, drop, or reuse facts.
let legacy_classifyHeapResource (semanticType: SemanticType) (environment: TypeEnvironment) = legacy_heapResourceBearing(semanticType)(environment)

let recursive legacy_heapAnyUnresolved (types: List(SemanticType)) (environment: TypeEnvironment) (path: List((Int, Str))) =
    match types with
        | [] -> false
        | head :: rest ->
            if legacy_heapContainsUnresolvedType(head)(environment)(path)
            then true
            else legacy_heapAnyUnresolved(rest)(environment)(path)
and legacy_heapAnyGroupedUnresolved (grouped: List((Str, List(SemanticType)))) (environment: TypeEnvironment) (path: List((Int, Str))) =
    match grouped with
        | [] -> false
        | (_constructorName, fieldTypes) :: rest ->
            if legacy_heapAnyUnresolved(fieldTypes)(environment)(path)
            then true
            else legacy_heapAnyGroupedUnresolved(rest)(environment)(path)
and legacy_heapContainsUnresolvedType (semanticType: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) =
    (let resolved = legacy_resolveLayoutType(semanticType)(environment)
    in
        match resolved with
            | SemVariable(_id) -> true
            | SemParameter(_id, _name) -> true
            | SemList(element) -> legacy_heapContainsUnresolvedType(element)(environment)(path)
            | SemTuple(elements) -> legacy_heapAnyUnresolved(elements)(environment)(path)
            | SemNamed(symbolId, name, arguments) ->
                if legacy_heapPathContains(symbolId)(name)(path)
                then false
                else
                    let extendedPath = (symbolId, name) :: path
                    in
                        if legacy_heapAnyUnresolved(arguments)(environment)(extendedPath)
                        then true
                        else
                            legacy_heapAnyGroupedUnresolved(legacy_heapNamedTypeFields(symbolId)(name)(arguments)(environment))(environment)(extendedPath)
            | _ -> false)

let legacy_heapDirectUnresolved semanticType =
    match semanticType with
        | SemVariable(_id) -> true
        | SemParameter(_id, _name) -> true
        | _ -> false

let legacy_heapUnresolvedType semanticType environment =
    match typeGraphAny(given (value) -> legacy_resolveLayoutType(value)(environment))(legacy_heapDirectUnresolved)(legacy_heapGraphChildren(true)(environment))(semanticType) with
        | Some(result) -> result
        | None ->
            typeGraphAnyWithPathCuts(given (value) -> legacy_resolveLayoutType(value)(environment))(legacy_heapDirectUnresolved)(legacy_heapGraphChildren(true)(environment))(semanticType)

// A named type the runtime may never manage as an ordinary reuse cell: a built-in resource handle
// or any aggregate that transitively owns one.
let legacy_heapAdtExcludedFromReuse (named: SemanticType) (environment: TypeEnvironment) =
    match named with
        | SemNamed(_symbolId, name, _arguments) -> legacy_heapBuiltinResourceTypeName(name) || legacy_heapResourceBearing(named)(environment)
        | _ -> true

// A named type declared by user source with no type parameters at this use site.
let legacy_heapMonomorphicUserAdt (named: SemanticType) =
    match named with
        | SemNamed(_symbolId, name, arguments) -> !legacy_heapBuiltinTypeName(name) && length(arguments) == 0
        | _ -> false

// Stage 0's `IsOpaqueHandleWord` beside the scalar layouts: a value of an external type with no
// destructor is a foreign handle the program only passes along, one word copied like an integer
// that needs no release; a declared resource owns what its handle names.
let legacy_canArenaResetLayoutIn (semanticType: SemanticType) (environment: TypeEnvironment) =
    match semanticType with
        | SemOpaque(name) ->
            environment
            |> inferenceTypeResolutionContext
            |> legacy_heapExternalTypeHasDestructor(name)
            |> (given (hasDestructor: Bool) -> hasDestructor == false)
        | other -> legacy_canArenaResetLayout(other)

type LegacyHeapDropResourceResult =
    | containsResource: Bool
    | resourceFree: List(SemanticType)

let recursive legacy_heapExactTypeSeen (semanticType: SemanticType) (types: List(SemanticType)) =
    match types with
        | [] -> false
        | previous :: rest -> previous == semanticType || legacy_heapExactTypeSeen(semanticType)(rest)

let recursive legacy_heapAddResourceFree (roots: List(SemanticType)) (known: List(SemanticType)) =
    match roots with
        | [] -> known
        | root :: rest ->
            if legacy_heapExactTypeSeen(root)(known)
            then legacy_heapAddResourceFree(rest)(known)
            else legacy_heapAddResourceFree(rest)(root :: known)

// The fresh query publishes only context-free negative facts. Its complete set
// can seed an index without a redundant whole-list deduplication pass.
let legacy_heapFreshResourceQuery (semanticType: SemanticType) (environment: TypeEnvironment) =
    match typeGraphAnyComplete(given (value) -> legacy_resolveLayoutType(value)(environment))(legacy_heapDirectResource(environment))(legacy_heapGraphChildren(false)(environment))(semanticType) with
        | TypeGraphClosureResult { answer = Some(false), completeRoots = roots } -> LegacyHeapDropResourceResult(containsResource = false, resourceFree = roots)
        | TypeGraphClosureResult { answer = Some(true) } -> LegacyHeapDropResourceResult(containsResource = true, resourceFree = [])
        | TypeGraphClosureResult { answer = None } ->
            let resource =
                typeGraphAnyWithPathCuts(given (value) -> legacy_resolveLayoutType(value)(environment))(legacy_heapDirectResource(environment))(legacy_heapGraphChildren(false)(environment))(semanticType)
            in
                LegacyHeapDropResourceResult(containsResource = resource, resourceFree = if resource
                then []
                else [semanticType])

let legacy_heapDropResourceQuery (semanticType: SemanticType) (environment: TypeEnvironment) (known: List(SemanticType)) =
    if legacy_heapExactTypeSeen(semanticType)(known)
    then LegacyHeapDropResourceResult(containsResource = false, resourceFree = known)
    else
        let result = legacy_heapFreshResourceQuery(semanticType)(environment)
        in LegacyHeapDropResourceResult(containsResource = result.containsResource, resourceFree = legacy_heapAddResourceFree(result.resourceFree)(known))

// Per-classification facts for one immutable environment. Only complete regular
// resource-free closures publish descendant negatives; nonregular fallback publishes
// only the queried root. Layout answers and their nominal cycle paths are never cached.
type LegacyHeapNamedRootInputs =
    | semanticType: SemanticType
    | grouped: List((Str, List(SemanticType)))
    | isRecord: Bool
    | containsResource: Bool
    | resourceFree: MapTree(Int, MapTree(Str, List(SemanticType)))

let legacy_heapResourceBucket (symbolId: Int) (name: Str) (index: MapTree(Int, MapTree(Str, List(SemanticType)))) =
    match Ashes.Collection.Map.getInt(symbolId)(index) with
        | None -> []
        | Some(byName) ->
            match Ashes.Collection.Map.getStr(name)(byName) with
                | None -> []
                | Some(types) -> types

let recursive legacy_heapIndexResourceFree (roots: List(SemanticType)) (index: MapTree(Int, MapTree(Str, List(SemanticType)))) =
    match roots with
        | [] -> index
        | root :: rest ->
            match root with
                | SemNamed(symbolId, name, _arguments) ->
                    let byName =
                        match Ashes.Collection.Map.getInt(symbolId)(index) with
                            | None -> Ashes.Collection.Map.empty
                            | Some(existing) -> existing
                    in
                        let bucket = legacy_heapResourceBucket(symbolId)(name)(index)
                        in
                            index
                            |> Ashes.Collection.Map.setInt(symbolId)(Ashes.Collection.Map.setStr(name)(root :: bucket)(byName))
                            |> legacy_heapIndexResourceFree(rest)
                | _ -> legacy_heapIndexResourceFree(rest)(index)

let legacy_heapNamedRootInputs (named: SemanticType) (environment: TypeEnvironment) =
    (let resource = legacy_heapFreshResourceQuery(named)(environment)
    in LegacyHeapNamedRootInputs(semanticType = named, grouped = legacy_heapNamedTypeConstructors(named)(environment), isRecord = legacy_heapNamedTypeIsRecord(named)(environment), containsResource = resource.containsResource, resourceFree = legacy_heapIndexResourceFree(resource.resourceFree)(Ashes.Collection.Map.empty)))

let legacy_heapRootConstructors (named: SemanticType) (environment: TypeEnvironment) (root: Maybe(LegacyHeapNamedRootInputs)) =
    match root with
        | Some(inputs) ->
            if inputs.semanticType == named
            then inputs.grouped
            else legacy_heapNamedTypeConstructors(named)(environment)
        | None -> legacy_heapNamedTypeConstructors(named)(environment)

let legacy_heapRootIsRecord (named: SemanticType) (environment: TypeEnvironment) (root: Maybe(LegacyHeapNamedRootInputs)) =
    match root with
        | Some(inputs) ->
            if inputs.semanticType == named
            then inputs.isRecord
            else legacy_heapNamedTypeIsRecord(named)(environment)
        | None -> legacy_heapNamedTypeIsRecord(named)(environment)

let legacy_heapRootResourceBearing (named: SemanticType) (environment: TypeEnvironment) (root: Maybe(LegacyHeapNamedRootInputs)) =
    match root with
        | Some(inputs) ->
            if inputs.semanticType == named
            then inputs.containsResource
            else
                match named with
                    | SemNamed(symbolId, name, _arguments) ->
                        if inputs.resourceFree
                        |> legacy_heapResourceBucket(symbolId)(name)
                        |> legacy_heapExactTypeSeen(named)
                        then false
                        else legacy_heapResourceBearing(named)(environment)
                    | _ -> legacy_heapResourceBearing(named)(environment)
        | None -> legacy_heapResourceBearing(named)(environment)

let legacy_heapRootExcludedFromReuse (named: SemanticType) (environment: TypeEnvironment) (root: Maybe(LegacyHeapNamedRootInputs)) =
    match named with
        | SemNamed(_symbolId, name, _arguments) -> legacy_heapBuiltinResourceTypeName(name) || legacy_heapRootResourceBearing(named)(environment)(root)
        | _ -> true

// Whether the arena copier can walk the whole graph: scalars and strings are leaves, lists and
// tuples recurse, and a named type recurses through every constructor field unless it owns a
// resource or the walk has already entered it.
let recursive legacy_heapArenaDeepCopyLayout (semanticType: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) = legacy_heapArenaDeepCopyLayoutWithRoot(semanticType)(environment)(path)(None)
and legacy_heapArenaDeepCopyLayoutWithRoot (semanticType: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) (root: Maybe(LegacyHeapNamedRootInputs)) =
    (let resolved = legacy_resolveLayoutType(semanticType)(environment)
    in
        if legacy_canArenaResetLayoutIn(resolved)(environment)
        then true
        else
            match resolved with
                | SemString -> true
                | SemBytes -> true
                | SemList(element) -> legacy_heapArenaDeepCopyLayoutWithRoot(element)(environment)(path)(root)
                | SemTuple(elements) ->
                    legacy_heapAllTypes(given (element) -> legacy_heapArenaDeepCopyLayoutWithRoot(element)(environment)(path)(root))(elements)
                | SemNamed(_symbolId, _name, _arguments) -> legacy_heapArenaDeepCopyAdtLayoutWithRoot(resolved)(environment)(path)(root)
                | _ -> false)
and legacy_heapArenaDeepCopyAdtLayout (named: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) = legacy_heapArenaDeepCopyAdtLayoutWithRoot(named)(environment)(path)(None)
and legacy_heapArenaDeepCopyAdtLayoutWithRoot (named: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) (root: Maybe(LegacyHeapNamedRootInputs)) =
    match named with
        | SemNamed(symbolId, name, _arguments) ->
            if legacy_heapPathContains(symbolId)(name)(path) || legacy_heapRootExcludedFromReuse(named)(environment)(root)
            then false
            else
                match legacy_heapRootConstructors(named)(environment)(root) with
                    | [] -> false
                    | grouped ->
                        legacy_heapAllGroupedFields(given (fieldType) -> legacy_heapArenaDeepCopyLayoutWithRoot(fieldType)(environment)((symbolId, name) :: path)(root))(grouped)
        | _ -> false

// Arena-copy consumers need this capability, not unrelated drop and reuse facts.
let legacy_classifyHeapArenaDeepCopy (semanticType: SemanticType) (environment: TypeEnvironment) =
    legacy_heapArenaDeepCopyLayout(legacy_resolveLayoutType(semanticType)(environment))(environment)([])

// A named type is shallow-copyable when every constructor has the same arity and every field is a
// scalar word; otherwise it is deep-copyable exactly when the arena copier can walk it.
let legacy_heapAdtShallowCopySupportedWithRoot (named: SemanticType) (environment: TypeEnvironment) (root: Maybe(LegacyHeapNamedRootInputs)) =
    match legacy_heapRootConstructors(named)(environment)(root) with
        | [] -> false
        | (_firstName, firstFields) :: _rest as grouped ->
            !legacy_heapRootResourceBearing(named)(environment)(root) && legacy_heapSameArity(length(firstFields))(grouped) && legacy_heapAllGroupedFields(given (fieldType) -> legacy_heapCanReset(fieldType)(environment))(grouped)

let legacy_heapAdtShallowCopySupported (named: SemanticType) (environment: TypeEnvironment) = legacy_heapAdtShallowCopySupportedWithRoot(named)(environment)(None)

let legacy_heapAdtStructuralCopyKind (named: SemanticType) (environment: TypeEnvironment) =
    (let grouped = legacy_heapNamedTypeConstructors(named)(environment)
    in
        match grouped with
            | [] -> NoStructuralCopy
            | (_firstName, firstFields) :: _rest ->
                if legacy_heapResourceBearing(named)(environment)
                then NoStructuralCopy
                else
                    if legacy_heapSameArity(length(firstFields))(grouped) && legacy_heapAllGroupedFields(given (fieldType) -> legacy_heapCanReset(fieldType)(environment))(grouped)
                    then ShallowCopy
                    else
                        if legacy_heapArenaDeepCopyAdtLayout(named)(environment)([])
                        then DeepCopy
                        else NoStructuralCopy)

let legacy_heapStructuralCopyKind (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let resolved = legacy_resolveLayoutType(semanticType)(environment)
    in
        if legacy_canArenaResetLayoutIn(resolved)(environment)
        then InlineCopy
        else
            match resolved with
                | SemString -> ShallowCopy
                | SemBytes -> ShallowCopy
                | SemBigInt -> ShallowCopy
                | SemList(element) ->
                    if legacy_heapArenaDeepCopyLayout(element)(environment)([])
                    then DeepCopy
                    else NoStructuralCopy
                | SemTuple(_elements) ->
                    if legacy_heapArenaDeepCopyLayout(resolved)(environment)([])
                    then DeepCopy
                    else NoStructuralCopy
                | SemNamed(_symbolId, _name, _arguments) -> legacy_heapAdtStructuralCopyKind(resolved)(environment)
                | _ -> NoStructuralCopy)

// A scope boundary asks only whether one shallow cell copy suffices. Preserve the full
// classifier's answer without computing deep-copy, child-copy, drop, or reuse layouts.
let legacy_classifyHeapShallowCopy (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let resolved = legacy_resolveLayoutType(semanticType)(environment)
    in
        if legacy_canArenaResetLayoutIn(resolved)(environment)
        then false
        else
            match resolved with
                | SemString -> true
                | SemBytes -> true
                | SemBigInt -> true
                | SemNamed(_symbolId, _name, _arguments) -> legacy_heapAdtShallowCopySupported(resolved)(environment)
                | _ -> false)

// The field rule shared by every drop and reuse layout: a scalar, a string-like leaf, a list of
// scalars, a tuple of such leaves, or a closure is always acceptable (a reference-counted closure
// releases its own environment through the dropper its construction attached, so it is a
// self-contained owned child regardless of what placed it there; an arena one needs no release at
// all), a named field is decided by the caller's own rule, and anything else (an opaque handle) is
// not.
let recursive legacy_heapDroppableLeafField (semanticType: SemanticType) (environment: TypeEnvironment) namedRule =
    (let resolved = legacy_resolveLayoutType(semanticType)(environment)
    in
        if legacy_canArenaResetLayoutIn(resolved)(environment)
        then true
        else
            match resolved with
                | SemString -> true
                | SemBytes -> true
                | SemBigInt -> true
                | SemList(element) -> legacy_heapDroppableLeafField(element)(environment)(namedRule)
                | SemTuple(elements) ->
                    legacy_heapAllTypes(given (element) -> legacy_heapDroppableLeafField(element)(environment)(namedRule))(elements)
                | SemFunction(_argument, _result, _capabilityRow) -> true
                | SemNamed(_symbolId, _name, _arguments) -> namedRule(resolved)
                | _ -> false)

// The drop query shares work only for exact resolved types. Paths retain nominal (id,name)
// cycle cuts; their arguments remain part of every memo key.
type LegacyHeapDropWork =
    | semanticType: SemanticType
    | path: List(SemanticType)

type LegacyHeapDropVisit =
    | semanticType: SemanticType
    | path: List(SemanticType)

let legacy_heapSameDropName (left: SemanticType) (right: SemanticType) =
    match (left, right) with
        | (SemNamed(leftId, leftName, _leftArguments), SemNamed(rightId, rightName, _rightArguments)) -> leftId == rightId && leftName == rightName
        | _ -> false

let recursive legacy_heapDropPathContains (semanticType: SemanticType) (path: List(SemanticType)) =
    match path with
        | [] -> false
        | previous :: rest -> legacy_heapSameDropName(semanticType)(previous) || legacy_heapDropPathContains(semanticType)(rest)

let recursive legacy_heapDropChangesInstantiation (semanticType: SemanticType) (path: List(SemanticType)) =
    match path with
        | [] -> false
        | previous :: rest ->
            if legacy_heapSameDropName(semanticType)(previous)
            then previous != semanticType
            else legacy_heapDropChangesInstantiation(semanticType)(rest)

let recursive legacy_heapDropPathSubset (lessBlocked: List(SemanticType)) (moreBlocked: List(SemanticType)) =
    match lessBlocked with
        | [] -> true
        | previous :: rest -> legacy_heapDropPathContains(previous)(moreBlocked) && legacy_heapDropPathSubset(rest)(moreBlocked)

let recursive legacy_heapDropCovered (semanticType: SemanticType) (path: List(SemanticType)) (pathCuts: Bool) (visited: List(LegacyHeapDropVisit)) =
    match visited with
        | [] -> false
        | LegacyHeapDropVisit { semanticType = previous, path = previousPath } :: rest -> previous == semanticType && (pathCuts == false || legacy_heapDropPathSubset(previousPath)(path)) || legacy_heapDropCovered(semanticType)(path)(pathCuts)(rest)

let recursive legacy_heapPrependDropWork (types: List(SemanticType)) (path: List(SemanticType)) (pending: List(LegacyHeapDropWork)) =
    match types with
        | [] -> pending
        | head :: rest -> LegacyHeapDropWork(semanticType = head, path = path) :: legacy_heapPrependDropWork(rest)(path)(pending)

type LegacyHeapDropSearchResult =
    | droppable: Maybe(Bool)
    | resourceFree: List(SemanticType)

// A regular graph visits each exact named type once. If instantiation changes on a path,
// restart with the original nominal cut and only share visits with fewer blocked ancestors.
// Every queued successor is processed before returning true; no partial drop answer is cached.
let recursive legacy_heapDropSearch (pending: List(LegacyHeapDropWork)) (visited: List(LegacyHeapDropVisit)) (known: List(SemanticType)) (environment: TypeEnvironment) (pathCuts: Bool) =
    match pending with
        | [] -> LegacyHeapDropSearchResult(droppable = Some(true), resourceFree = known)
        | LegacyHeapDropWork { semanticType = semanticType, path = path } :: rest ->
            let resolved = legacy_resolveLayoutType(semanticType)(environment)
            in
                if legacy_canArenaResetLayoutIn(resolved)(environment)
                then legacy_heapDropSearch(rest)(visited)(known)(environment)(pathCuts)
                else
                    match resolved with
                        | SemString -> legacy_heapDropSearch(rest)(visited)(known)(environment)(pathCuts)
                        | SemBytes -> legacy_heapDropSearch(rest)(visited)(known)(environment)(pathCuts)
                        | SemBigInt -> legacy_heapDropSearch(rest)(visited)(known)(environment)(pathCuts)
                        | SemFunction(_argument, _result, _row) -> legacy_heapDropSearch(rest)(visited)(known)(environment)(pathCuts)
                        | SemList(element) -> legacy_heapDropSearch(LegacyHeapDropWork(semanticType = element, path = path) :: rest)(visited)(known)(environment)(pathCuts)
                        | SemTuple(elements) ->
                            legacy_heapDropSearch(legacy_heapPrependDropWork(elements)(path)(rest))(visited)(known)(environment)(pathCuts)
                        | SemNamed(_symbolId, _name, _arguments) ->
                            match legacy_heapDropResourceQuery(resolved)(environment)(known) with
                                | LegacyHeapDropResourceResult { containsResource = true, resourceFree = nextKnown } -> LegacyHeapDropSearchResult(droppable = Some(false), resourceFree = nextKnown)
                                | LegacyHeapDropResourceResult { containsResource = false, resourceFree = nextKnown } ->
                                    if pathCuts == false && legacy_heapDropChangesInstantiation(resolved)(path)
                                    then LegacyHeapDropSearchResult(droppable = None, resourceFree = nextKnown)
                                    else
                                        if legacy_heapDropPathContains(resolved)(path) || legacy_heapDropCovered(resolved)(path)(pathCuts)(visited)
                                        then legacy_heapDropSearch(rest)(visited)(nextKnown)(environment)(pathCuts)
                                        else
                                            let children = legacy_heapGraphChildren(false)(environment)(resolved)
                                            in
                                                legacy_heapDropSearch(legacy_heapPrependDropWork(children)(resolved :: path)(rest))(LegacyHeapDropVisit(semanticType = resolved, path = path) :: visited)(nextKnown)(environment)(pathCuts)
                        | _ -> LegacyHeapDropSearchResult(droppable = Some(false), resourceFree = known)

let legacy_heapCanDropValueGraph (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let work = [LegacyHeapDropWork(semanticType = semanticType, path = [])]
    in
        match legacy_heapDropSearch(work)([])([])(environment)(false) with
            | LegacyHeapDropSearchResult { droppable = Some(result) } -> result
            | LegacyHeapDropSearchResult { droppable = None, resourceFree = known } ->
                match legacy_heapDropSearch(work)([])(known)(environment)(true) with
                    | LegacyHeapDropSearchResult { droppable = Some(result) } -> result
                    | LegacyHeapDropSearchResult { droppable = None } -> false)

// Query the existing path-sensitive graph verdict without constructing unrelated layout facts.
let legacy_classifyHeapOwnedChildrenDroppable (semanticType: SemanticType) (environment: TypeEnvironment) =
    legacy_heapCanDropValueGraph(legacy_resolveLayoutType(semanticType)(environment))(environment)

// A copy ADT: every field of every constructor is a scalar word.
let legacy_heapRuntimeCopyAdtLayout (named: SemanticType) (environment: TypeEnvironment) =
    match legacy_heapNamedTypeConstructors(named)(environment) with
        | [] -> false
        | grouped ->
            if legacy_heapAdtExcludedFromReuse(named)(environment)
            then false
            else
                legacy_heapAllGroupedFields(given (fieldType) -> legacy_heapCanReset(fieldType)(environment))(grouped)

// A TCO owned-child field: a scalar word or a list of scalar words.
let legacy_heapTcoOwnedChildField (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let resolved = legacy_resolveLayoutType(semanticType)(environment)
    in
        if legacy_canArenaResetLayoutIn(resolved)(environment)
        then true
        else
            match resolved with
                | SemList(element) -> legacy_heapCanReset(element)(environment)
                | _ -> false)

let legacy_heapOwnsHeapField (semanticType: SemanticType) (environment: TypeEnvironment) = !legacy_heapCanReset(semanticType)(environment)

// A record ADT: a single named-field constructor whose fields a runtime-managed cell owns, not
// already on the path.
let recursive legacy_heapRuntimeRecordAdtLayout (named: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) = legacy_heapRuntimeRecordAdtLayoutWithRoot(named)(environment)(path)(None)
and legacy_heapRuntimeRecordAdtLayoutWithRoot (named: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) (root: Maybe(LegacyHeapNamedRootInputs)) =
    match named with
        | SemNamed(symbolId, name, _arguments) ->
            match legacy_heapRootConstructors(named)(environment)(root) with
                | (_constructorName, fieldTypes) :: [] ->
                    if !legacy_heapRootIsRecord(named)(environment)(root) || legacy_heapRootExcludedFromReuse(named)(environment)(root) || legacy_heapPathContains(symbolId)(name)(path)
                    then false
                    else
                        legacy_heapAllTypes(given (fieldType) -> legacy_heapRuntimeOwnedFieldLayoutWithRoot(fieldType)(environment)((symbolId, name) :: path)(root))(fieldTypes)
                | _ -> false
        | _ -> false
// A field a runtime-managed record or variant owns, stage 0's `IsRuntimeOwnedFieldLayout`: a
// scalar, a string, bytes, or a big integer, a list or tuple of such fields, a record, variant,
// or positional type of them, or a reference-counted closure. A type that reaches itself is
// rejected, since the inline runtime-managed copy of such a graph would not terminate.
and legacy_heapRuntimeOwnedFieldLayout (semanticType: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) = legacy_heapRuntimeOwnedFieldLayoutWithRoot(semanticType)(environment)(path)(None)
and legacy_heapRuntimeOwnedFieldLayoutWithRoot (semanticType: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) (root: Maybe(LegacyHeapNamedRootInputs)) =
    (let resolved = legacy_resolveLayoutType(semanticType)(environment)
    in
        if legacy_canArenaResetLayoutIn(resolved)(environment)
        then true
        else
            match resolved with
                | SemString -> true
                | SemBytes -> true
                | SemBigInt -> true
                | SemList(element) -> legacy_heapRuntimeOwnedFieldLayoutWithRoot(element)(environment)(path)(root)
                | SemTuple(elements) ->
                    legacy_heapAllTypes(given (element) -> legacy_heapRuntimeOwnedTupleElementLayoutWithRoot(element)(environment)(path)(root))(elements)
                | SemFunction(_argument, _result, _capabilityRow) -> true
                | SemNamed(_symbolId, _name, _arguments) -> legacy_heapAdtShallowCopySupportedWithRoot(resolved)(environment)(root) || legacy_heapRuntimeRecordAdtLayoutWithRoot(resolved)(environment)(path)(root) || legacy_heapRuntimeOwnedChildAdtLayoutWithRoot(resolved)(environment)(path)(root) || legacy_heapRuntimeTcoOwnedChildAdtLayoutWithRoot(resolved)(environment)(path)(root) || legacy_heapRuntimePositionalAdtLayoutWithRoot(resolved)(environment)(path)(root)
                | _ -> false)
// A tuple element a runtime-managed tuple owns, stage 0's `IsRuntimeOwnedTupleElementLayout`:
// an owned field other than a closure, whose placement the static tuple rules never trust.
and legacy_heapRuntimeOwnedTupleElementLayout (element: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) = legacy_heapRuntimeOwnedTupleElementLayoutWithRoot(element)(environment)(path)(None)
and legacy_heapRuntimeOwnedTupleElementLayoutWithRoot (element: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) (root: Maybe(LegacyHeapNamedRootInputs)) =
    match legacy_resolveLayoutType(element)(environment) with
        | SemFunction(_argument, _result, _capabilityRow) -> false
        | _ -> legacy_heapRuntimeOwnedFieldLayoutWithRoot(element)(environment)(path)(root)
// A positional single-constructor type with at least one owned field, every field one a
// runtime-managed cell owns, stage 0's `IsRuntimePositionalAdtLayout`: a child a record or
// variant owns and a list element, outside the outer-cell reuse and record paths.
and legacy_heapRuntimePositionalAdtLayout (named: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) = legacy_heapRuntimePositionalAdtLayoutWithRoot(named)(environment)(path)(None)
and legacy_heapRuntimePositionalAdtLayoutWithRoot (named: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) (root: Maybe(LegacyHeapNamedRootInputs)) =
    match named with
        | SemNamed(symbolId, name, _arguments) ->
            match legacy_heapRootConstructors(named)(environment)(root) with
                | (_constructorName, fieldTypes) :: [] ->
                    if legacy_heapBuiltinTypeName(name) || legacy_heapRootIsRecord(named)(environment)(root) || legacy_heapRootExcludedFromReuse(named)(environment)(root) || legacy_heapPathContains(symbolId)(name)(path)
                    then false
                    else
                        legacy_heapAllTypes(given (fieldType) -> legacy_heapRuntimeOwnedFieldLayoutWithRoot(fieldType)(environment)((symbolId, name) :: path)(root))(fieldTypes) && legacy_heapAnyType(given (fieldType) -> legacy_heapOwnsHeapField(fieldType)(environment))(fieldTypes)
                | _ -> false
        | _ -> false
// An owned-child ADT: a non-builtin type with at least two constructors and at least one
// heap-owning field, every field one a runtime-managed cell owns, not already on the path. A
// single unnamed-field constructor is instead the TCO owned-child layout: every heap-owning
// field a list of scalars, with at least one such field.
and legacy_heapRuntimeOwnedChildAdtLayout (named: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) = legacy_heapRuntimeOwnedChildAdtLayoutWithRoot(named)(environment)(path)(None)
and legacy_heapRuntimeOwnedChildAdtLayoutWithRoot (named: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) (root: Maybe(LegacyHeapNamedRootInputs)) =
    match named with
        | SemNamed(symbolId, name, _arguments) ->
            let grouped = legacy_heapRootConstructors(named)(environment)(root)
            in
                if legacy_heapBuiltinResourceTypeName(name) || !(length(grouped) >= 2) || legacy_heapRootExcludedFromReuse(named)(environment)(root) || legacy_heapPathContains(symbolId)(name)(path)
                then false
                else
                    legacy_heapAllGroupedFields(given (fieldType) -> legacy_heapRuntimeOwnedFieldLayoutWithRoot(fieldType)(environment)((symbolId, name) :: path)(root))(grouped) && legacy_heapAnyGroupedField(given (fieldType) -> legacy_heapOwnsHeapField(fieldType)(environment))(grouped)
        | _ -> false
and legacy_heapRuntimeTcoOwnedChildAdtLayout (named: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) = legacy_heapRuntimeTcoOwnedChildAdtLayoutWithRoot(named)(environment)(path)(None)
and legacy_heapRuntimeTcoOwnedChildAdtLayoutWithRoot (named: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) (root: Maybe(LegacyHeapNamedRootInputs)) =
    match legacy_heapRootConstructors(named)(environment)(root) with
        | (_constructorName, fieldTypes) :: [] ->
            if legacy_heapRootIsRecord(named)(environment)(root)
            then legacy_heapRuntimeOwnedChildAdtLayoutWithRoot(named)(environment)(path)(root)
            else
                if !legacy_heapMonomorphicUserAdt(named) || legacy_heapRootExcludedFromReuse(named)(environment)(root)
                then false
                else
                    legacy_heapAllTypes(given (fieldType) -> legacy_heapTcoOwnedChildField(fieldType)(environment))(fieldTypes) && legacy_heapAnyType(given (fieldType) -> legacy_heapOwnsHeapField(fieldType)(environment))(fieldTypes)
        | _ -> legacy_heapRuntimeOwnedChildAdtLayoutWithRoot(named)(environment)(path)(root)

// A recursive-copy field: a scalar word or a direct reference to the enclosing type.
let legacy_heapRecursiveCopyField (symbolId: Int) (name: Str) (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let resolved = legacy_resolveLayoutType(semanticType)(environment)
    in
        if legacy_canArenaResetLayoutIn(resolved)(environment)
        then true
        else
            match resolved with
                | SemNamed(childId, childName, _arguments) -> childId == symbolId && childName == name
                | _ -> false)

// The first recursive runtime-RC boundary: a monomorphic user ADT whose fields are scalar words or
// direct references to the same type, with at least one such self-reference.
let legacy_heapRuntimeRecursiveCopyAdtLayout (named: SemanticType) (environment: TypeEnvironment) =
    match named with
        | SemNamed(symbolId, name, _arguments) ->
            match legacy_heapNamedTypeConstructors(named)(environment) with
                | [] -> false
                | grouped ->
                    if !legacy_heapMonomorphicUserAdt(named) || legacy_heapAdtExcludedFromReuse(named)(environment)
                    then false
                    else
                        legacy_heapAllGroupedFields(given (fieldType) -> legacy_heapRecursiveCopyField(symbolId)(name)(fieldType)(environment))(grouped) && legacy_heapAnyGroupedField(given (fieldType) -> legacy_heapOwnsHeapField(fieldType)(environment))(grouped)
        | _ -> false

// Whether a list element type can live in a runtime-managed TCO accumulator list: scalars and
// string-like leaves, lists and tuples of such elements, and shallow-copy, record, or owned-child
// ADTs.
let recursive legacy_heapRuntimeTcoListElementLayout (semanticType: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) =
    (let resolved = legacy_resolveLayoutType(semanticType)(environment)
    in
        if legacy_canArenaResetLayoutIn(resolved)(environment)
        then true
        else
            match resolved with
                | SemString -> true
                | SemBytes -> true
                | SemBigInt -> true
                | SemList(element) -> legacy_heapRuntimeTcoListElementLayout(element)(environment)(path)
                | SemTuple(elements) ->
                    legacy_heapAllTypes(given (element) -> legacy_heapRuntimeTcoListElementLayout(element)(environment)(path))(elements)
                | SemNamed(_symbolId, _name, _arguments) -> legacy_heapAdtShallowCopySupported(resolved)(environment) || legacy_heapRuntimeRecordAdtLayout(resolved)(environment)([]) || legacy_heapRuntimeOwnedChildAdtLayout(resolved)(environment)(path) || legacy_heapRuntimePositionalAdtLayout(resolved)(environment)(path)
                | _ -> false)

let legacy_heapDropKind (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let resolved = legacy_resolveLayoutType(semanticType)(environment)
    in
        if legacy_canArenaResetLayoutIn(resolved)(environment)
        then NoChildDrop
        else
            match resolved with
                | SemString -> DropString
                | SemBytes -> DropBytes
                | SemBigInt -> DropBigInt
                | SemList(_element) -> DropList
                | SemTuple(_elements) -> DropTuple
                | SemNamed(_symbolId, _name, _arguments) -> DropAdt
                | SemFunction(_argument, _result, _capabilityRow) -> DropClosure
                | _ -> UnsupportedChildDrop)

// The children of one constructor's fields (`None` for a list or tuple), in field order, ahead of
// `tail`: built straight onto it rather than from a list of pairs and a concatenation.
let recursive legacy_heapChildrenOnto (constructorName: Maybe(Str)) (fieldTypes: List(SemanticType)) (index: Int) (environment: TypeEnvironment) (tail: List(HeapLayoutChild)) =
    match fieldTypes with
        | [] -> tail
        | fieldType :: rest ->
            HeapLayoutChild(
                constructorName = constructorName,
                fieldIndex = index,
                childType = fieldType,
                dropKind = legacy_heapDropKind(fieldType)(environment),
                copyKind = legacy_heapStructuralCopyKind(fieldType)(environment)
            ) :: legacy_heapChildrenOnto(constructorName)(rest)(index + 1)(environment)(tail)

let recursive legacy_heapNamedTypeChildren (grouped: List((Str, List(SemanticType)))) (environment: TypeEnvironment) =
    match grouped with
        | [] -> []
        | (constructorName, fieldTypes) :: rest ->
            environment
            |> legacy_heapNamedTypeChildren(rest)
            |> legacy_heapChildrenOnto(Some(constructorName))(fieldTypes)(0)(environment)

let legacy_heapLayoutChildren (resolved: SemanticType) (environment: TypeEnvironment) =
    match resolved with
        | SemList(element) -> legacy_heapChildrenOnto(None)([element, resolved])(0)(environment)([])
        | SemTuple(elements) -> legacy_heapChildrenOnto(None)(elements)(0)(environment)([])
        | SemNamed(_symbolId, _name, _arguments) ->
            legacy_heapNamedTypeChildren(legacy_heapNamedTypeConstructors(resolved)(environment))(environment)
        | _ -> []

// Exact projections of full layout classification, without unrelated root facts.
let legacy_classifyHeapChildren (semanticType: SemanticType) (environment: TypeEnvironment) =
    legacy_heapLayoutChildren(legacy_resolveLayoutType(semanticType)(environment))(environment)

let recursive legacy_heapNamedChildTypes (grouped: List((Str, List(SemanticType)))) =
    match grouped with
        | [] -> []
        | (_constructorName, fieldTypes) :: rest ->
            rest
            |> legacy_heapNamedChildTypes
            |> append(fieldTypes)

// Preserve the list's self/tail edge as well as constructor and field order.
// A graph-containment child query intentionally omits that edge and is not equivalent.
let legacy_classifyHeapChildTypes (semanticType: SemanticType) (environment: TypeEnvironment) =
    match legacy_resolveLayoutType(semanticType)(environment) with
        | SemList(element) as resolved -> [element, resolved]
        | SemTuple(elements) -> elements
        | SemNamed(_symbolId, _name, _arguments) as resolved ->
            environment
            |> legacy_heapNamedTypeConstructors(resolved)
            |> legacy_heapNamedChildTypes
        | _ -> []

let recursive legacy_heapAdmissibilityChildrenOnto (fieldTypes: List(SemanticType)) (environment: TypeEnvironment) (tail: List((SemanticType, HeapChildDropKind))) =
    match fieldTypes with
        | [] -> tail
        | fieldType :: rest -> (fieldType, legacy_heapDropKind(fieldType)(environment)) :: legacy_heapAdmissibilityChildrenOnto(rest)(environment)(tail)

let recursive legacy_heapNamedAdmissibilityChildren grouped environment =
    match grouped with
        | [] -> []
        | (_constructorName, fieldTypes) :: rest ->
            environment
            |> legacy_heapNamedAdmissibilityChildren(rest)
            |> legacy_heapAdmissibilityChildrenOnto(fieldTypes)(environment)

let legacy_heapAdmissibilityChildren (resolved: SemanticType) (environment: TypeEnvironment) =
    match resolved with
        | SemList(element) -> legacy_heapAdmissibilityChildrenOnto([element, resolved])(environment)([])
        | SemTuple(elements) -> legacy_heapAdmissibilityChildrenOnto(elements)(environment)([])
        | SemNamed(_symbolId, _name, _arguments) ->
            legacy_heapNamedAdmissibilityChildren(legacy_heapNamedTypeConstructors(resolved)(environment))(environment)
        | _ -> []

let legacy_classifyHeapAdmissibility (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let resolved = legacy_resolveLayoutType(semanticType)(environment)
    in
        HeapAdmissibilityFacts(
            containsResource = legacy_heapResourceBearing(resolved)(environment),
            containsUnresolvedType = legacy_heapUnresolvedType(resolved)(environment),
            children = legacy_heapAdmissibilityChildren(resolved)(environment)
        ))

// Copy-plan consumers need drop ownership and field order, but never child copy kinds or
// root droppability/resource/rejection facts. Keep this a distinct, complete query result.
let recursive legacy_projectHeapCopyPlanChildren (children: List(HeapLayoutChild)) =
    match children with
        | [] -> []
        | HeapLayoutChild { constructorName = name, fieldIndex = index, childType = childType, dropKind = dropKind } :: rest -> HeapCopyPlanChild(constructorName = name, fieldIndex = index, childType = childType, dropKind = dropKind) :: legacy_projectHeapCopyPlanChildren(rest)

let legacy_projectHeapCopyPlanFacts (facts: HeapLayoutFacts) =
    HeapCopyPlanFacts(
        shallowCopy = facts.structuralCopy == ShallowCopy,
        arenaDeepCopySupported = facts.arenaDeepCopySupported,
        runtimeRecordAdtSupported = facts.runtimeRecordAdtSupported,
        runtimeOwnedChildAdtSupported = facts.runtimeOwnedChildAdtSupported,
        runtimeTcoOwnedChildAdtSupported = facts.runtimeTcoOwnedChildAdtSupported,
        runtimePositionalAdtSupported = facts.runtimePositionalAdtSupported,
        children = legacy_projectHeapCopyPlanChildren(facts.children)
    )

let recursive legacy_heapCopyPlanChildrenOnto (name: Maybe(Str)) (fields: List(SemanticType)) (index: Int) (environment: TypeEnvironment) (tail: List(HeapCopyPlanChild)) =
    match fields with
        | [] -> tail
        | fieldType :: rest -> HeapCopyPlanChild(constructorName = name, fieldIndex = index, childType = fieldType, dropKind = legacy_heapDropKind(fieldType)(environment)) :: legacy_heapCopyPlanChildrenOnto(name)(rest)(index + 1)(environment)(tail)

let recursive legacy_heapNamedCopyPlanChildren (grouped: List((Str, List(SemanticType)))) (environment: TypeEnvironment) =
    match grouped with
        | [] -> []
        | (name, fields) :: rest ->
            environment
            |> legacy_heapNamedCopyPlanChildren(rest)
            |> legacy_heapCopyPlanChildrenOnto(Some(name))(fields)(0)(environment)

let legacy_heapCopyPlanChildren (resolved: SemanticType) (environment: TypeEnvironment) =
    match resolved with
        | SemList(element) -> legacy_heapCopyPlanChildrenOnto(None)([element, resolved])(0)(environment)([])
        | SemTuple(elements) -> legacy_heapCopyPlanChildrenOnto(None)(elements)(0)(environment)([])
        | SemNamed(_id, _name, _args) ->
            legacy_heapNamedCopyPlanChildren(legacy_heapNamedTypeConstructors(resolved)(environment))(environment)
        | _ -> []

let legacy_heapClassificationRoot resolved environment =
    match resolved with
        | SemNamed(_symbolId, _name, _arguments) ->
            environment
            |> legacy_heapNamedRootInputs(resolved)
            |> Some
        | _ -> None

let legacy_heapRootShallowCopy resolved environment root =
    match root with
        | Some(_inputs) ->
            if legacy_canArenaResetLayoutIn(resolved)(environment)
            then false
            else legacy_heapAdtShallowCopySupportedWithRoot(resolved)(environment)(root)
        | None -> legacy_classifyHeapShallowCopy(resolved)(environment)

let legacy_heapRootArenaDeepCopy resolved environment root =
    match root with
        | Some(_inputs) -> legacy_heapArenaDeepCopyAdtLayoutWithRoot(resolved)(environment)([])(root)
        | None -> legacy_heapArenaDeepCopyLayout(resolved)(environment)([])

let legacy_classifyHeapCopyPlan (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let resolved = legacy_resolveLayoutType(semanticType)(environment)
    in
        let root = legacy_heapClassificationRoot(resolved)(environment)
        in
            HeapCopyPlanFacts(
                shallowCopy = legacy_heapRootShallowCopy(resolved)(environment)(root),
                arenaDeepCopySupported = legacy_heapRootArenaDeepCopy(resolved)(environment)(root),
                runtimeRecordAdtSupported = legacy_heapRuntimeRecordAdtLayoutWithRoot(resolved)(environment)([])(root),
                runtimeOwnedChildAdtSupported = legacy_heapRuntimeOwnedChildAdtLayoutWithRoot(resolved)(environment)([])(root),
                runtimeTcoOwnedChildAdtSupported = legacy_heapRuntimeTcoOwnedChildAdtLayoutWithRoot(resolved)(environment)([])(root),
                runtimePositionalAdtSupported = legacy_heapRuntimePositionalAdtLayoutWithRoot(resolved)(environment)([])(root),
                children = legacy_heapCopyPlanChildren(resolved)(environment)
            ))

let recursive legacy_heapAnyOwnedChild (children: List(HeapLayoutChild)) =
    match children with
        | [] -> false
        | HeapLayoutChild { dropKind = dropKind } :: rest ->
            if dropKind == NoChildDrop
            then legacy_heapAnyOwnedChild(rest)
            else true

// Only a named type has an outer cell the runtime can reuse; every other shape declines.
let legacy_heapRuntimeReuseSupport (resolved: SemanticType) (environment: TypeEnvironment) =
    match resolved with
        | SemNamed(_symbolId, _name, _arguments) ->
            LegacyHeapRuntimeReuseSupport(
                copyAdt = legacy_heapRuntimeCopyAdtLayout(resolved)(environment),
                recordAdt = legacy_heapRuntimeRecordAdtLayout(resolved)(environment)([]),
                ownedChildAdt = legacy_heapRuntimeOwnedChildAdtLayout(resolved)(environment)([]),
                tcoOwnedChildAdt = legacy_heapRuntimeTcoOwnedChildAdtLayout(resolved)(environment)([]),
                recursiveAdt = legacy_heapRuntimeRecursiveCopyAdtLayout(resolved)(environment)
            )
        | _ -> LegacyHeapRuntimeReuseSupport(copyAdt = false, recordAdt = false, ownedChildAdt = false, tcoOwnedChildAdt = false, recursiveAdt = false)

let legacy_heapOuterCellReuseSupported (support: LegacyHeapRuntimeReuseSupport) =
    match support with
        | LegacyHeapRuntimeReuseSupport { copyAdt = copyAdt, recordAdt = recordAdt, ownedChildAdt = ownedChildAdt, recursiveAdt = recursiveAdt } -> copyAdt || recordAdt || ownedChildAdt || recursiveAdt

let legacy_heapRejections (containsResource: Bool) (ownedChildrenDroppable: Bool) (containsUnresolvedType: Bool) (runtimeOuterCellReuseSupported: Bool) =
    HeapLayoutRejections(
        resourceOrBorrowedViewContainment = containsResource,
        unsupportedChildDropLayout = !ownedChildrenDroppable,
        unresolvedType = containsUnresolvedType,
        unsupportedOuterCellReuse = !runtimeOuterCellReuseSupported
    )

let legacy_heapAssembleFacts (resolved: SemanticType) (environment: TypeEnvironment) (children: List(HeapLayoutChild)) (containsResource: Bool) (ownedChildrenDroppable: Bool) (containsUnresolvedType: Bool) (support: LegacyHeapRuntimeReuseSupport) =
    match support with
        | LegacyHeapRuntimeReuseSupport { copyAdt = copyAdt, recordAdt = recordAdt, ownedChildAdt = ownedChildAdt, tcoOwnedChildAdt = tcoOwnedChildAdt } ->
            HeapLayoutFacts(
                containsResource = containsResource,
                containsUnresolvedType = containsUnresolvedType,
                containsOwnedChild = legacy_heapAnyOwnedChild(children),
                structuralCopy = legacy_heapStructuralCopyKind(resolved)(environment),
                arenaDeepCopySupported = legacy_heapArenaDeepCopyLayout(resolved)(environment)([]),
                ownedChildrenDroppable = ownedChildrenDroppable,
                runtimeOuterCellReuseSupported = legacy_heapOuterCellReuseSupported(support),
                runtimeCopyAdtSupported = copyAdt,
                runtimeRecordAdtSupported = recordAdt,
                runtimeOwnedChildAdtSupported = ownedChildAdt,
                runtimeTcoOwnedChildAdtSupported = tcoOwnedChildAdt,
                runtimeTcoListElementSupported = legacy_heapRuntimeTcoListElementLayout(resolved)(environment)([]),
                runtimePositionalAdtSupported = legacy_heapRuntimePositionalAdtLayout(resolved)(environment)([]),
                children = children,
                rejections = support
                |> legacy_heapOuterCellReuseSupported
                |> legacy_heapRejections(containsResource)(ownedChildrenDroppable)(containsUnresolvedType)
            )

let legacy_heapLayoutFactsOf (environment: TypeEnvironment) (resolved: SemanticType) =
    environment
    |> legacy_heapRuntimeReuseSupport(resolved)
    |> legacy_heapAssembleFacts(resolved)(environment)(legacy_heapLayoutChildren(resolved)(environment))(legacy_heapResourceBearing(resolved)(environment))(legacy_heapCanDropValueGraph(resolved)(environment))(legacy_heapUnresolvedType(resolved)(environment))

let legacy_classifyHeapLayout (semanticType: SemanticType) (environment: TypeEnvironment) =
    environment
    |> legacy_resolveLayoutType(semanticType)
    |> legacy_heapLayoutFactsOf(environment)

// Drop emission never consumes child copy kinds or the unrelated whole-layout support fields.
let legacy_projectDropperChild (child: HeapLayoutChild) = HeapDropperChild(constructorName = child.constructorName, fieldIndex = child.fieldIndex, childType = child.childType, dropKind = child.dropKind)

let legacy_projectHeapDropperFacts (facts: HeapLayoutFacts) =
    HeapDropperFacts(
        containsResource = facts.containsResource,
        containsUnresolvedType = facts.containsUnresolvedType,
        containsOwnedChild = facts.containsOwnedChild,
        shallowCopy = facts.structuralCopy == ShallowCopy,
        runtimeRecordAdtSupported = facts.runtimeRecordAdtSupported,
        runtimeOwnedChildAdtSupported = facts.runtimeOwnedChildAdtSupported,
        runtimeTcoOwnedChildAdtSupported = facts.runtimeTcoOwnedChildAdtSupported,
        runtimePositionalAdtSupported = facts.runtimePositionalAdtSupported,
        children = map(legacy_projectDropperChild)(facts.children)
    )

let recursive legacy_dropperChildrenOnto constructorName fieldTypes index environment tail =
    match fieldTypes with
        | [] -> tail
        | fieldType :: rest -> HeapDropperChild(constructorName = constructorName, fieldIndex = index, childType = fieldType, dropKind = legacy_heapDropKind(fieldType)(environment)) :: legacy_dropperChildrenOnto(constructorName)(rest)(index + 1)(environment)(tail)

let recursive legacy_namedDropperChildren grouped environment =
    match grouped with
        | [] -> []
        | (name, fields) :: rest ->
            environment
            |> legacy_namedDropperChildren(rest)
            |> legacy_dropperChildrenOnto(Some(name))(fields)(0)(environment)

let legacy_dropperChildren resolved environment =
    match resolved with
        | SemList(element) -> legacy_dropperChildrenOnto(None)([element, resolved])(0)(environment)([])
        | SemTuple(elements) -> legacy_dropperChildrenOnto(None)(elements)(0)(environment)([])
        | SemNamed(_id, _name, _arguments) ->
            legacy_namedDropperChildren(legacy_heapNamedTypeConstructors(resolved)(environment))(environment)
        | _ -> []

let recursive legacy_anyDropperOwnedChild (children: List(HeapDropperChild)) =
    match children with
        | [] -> false
        | child :: rest -> child.dropKind != NoChildDrop || legacy_anyDropperOwnedChild(rest)

let legacy_classifyHeapDropper (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let resolved = legacy_resolveLayoutType(semanticType)(environment)
    in
        let root = legacy_heapClassificationRoot(resolved)(environment)
        in
            let children = legacy_dropperChildren(resolved)(environment)
            in
                HeapDropperFacts(
                    containsResource = legacy_heapRootResourceBearing(resolved)(environment)(root),
                    containsUnresolvedType = legacy_heapUnresolvedType(resolved)(environment),
                    containsOwnedChild = legacy_anyDropperOwnedChild(children),
                    shallowCopy = legacy_heapRootShallowCopy(resolved)(environment)(root),
                    runtimeRecordAdtSupported = legacy_heapRuntimeRecordAdtLayoutWithRoot(resolved)(environment)([])(root),
                    runtimeOwnedChildAdtSupported = legacy_heapRuntimeOwnedChildAdtLayoutWithRoot(resolved)(environment)([])(root),
                    runtimeTcoOwnedChildAdtSupported = legacy_heapRuntimeTcoOwnedChildAdtLayoutWithRoot(resolved)(environment)([])(root),
                    runtimePositionalAdtSupported = legacy_heapRuntimePositionalAdtLayoutWithRoot(resolved)(environment)([])(root),
                    children = children
                ))
