// Immutable, environment-scoped storage for lazily discovered runtime type graph nodes.
// A node owns complete type roots through ordinary RC. Indexing does not traverse descendants:
// nonregular generic recursion can have infinitely many distinct concrete instantiations.
// Facts are distinct from adjacency and may only describe completed, whole-root queries.

import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeResolution
import Ashes.Collection.List.append
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import Ashes.Collection.List.length
import Ashes.Collection.Map.MapTree
export (
    type TypeGraphNode(..),
    type TypeGraphIndex(..),
    type TypeGraphFactKind(..),
    value emptyTypeGraphIndex,
    value compareTypeGraphKeys,
    value typeGraphFingerprint,
    value typeGraphNode,
    value typeGraphLookupNode,
    value typeGraphRootFact,
    value completeTypeGraphRootFact,
    value completeTypeGraphNegativeRoots,
    value typeGraphConstructorFieldGroups,
    value typeGraphExtendConstructorEnvironment,
    value typeGraphNamedTypeConstructors,
    value typeGraphNamedTypeFields,
    value typeGraphConstructorBuilds,
    value typeGraphEnvironmentConstructors,
    value typeGraphNamedTypeIsRecord,
)

let typeGraphEnvironmentConstructors (environment: TypeEnvironment) =
    match environment with
        | TypeEnvironment { constructors = constructors } -> constructors

// Peels a constructor's curried scheme body (a1 -> a2 -> ... -> Named(...)) into its field
// types and the final, still type-parameterized, result type.
let recursive heapConstructorFieldShape (body: SemanticType) (reversedParameters: List(SemanticType)) =
    match body with
        | SemFunction(parameter, result, None) -> heapConstructorFieldShape(result)(parameter :: reversedParameters)
        | _ -> (reverse(reversedParameters), body)

// Maps each of the type's own declared parameters — ordinary fresh type variables at the point a
// TypeDecl registers its constructors, per ProgramInference.ash's registerTypeParameters, not a
// distinct rigid-parameter shape — as they appear in a constructor's generalized result type, to
// the corresponding concrete argument at this use site.
let recursive heapParameterMapping (resultArguments: List(SemanticType)) (concreteArguments: List(SemanticType)) =
    match (resultArguments, concreteArguments) with
        | ([], _) -> []
        | (_, []) -> []
        | (SemVariable(variableId) :: restResult, concreteType :: restConcrete) -> (variableId, concreteType) :: heapParameterMapping(restResult)(restConcrete)
        | (_unmapped :: restResult, _concreteType :: restConcrete) -> heapParameterMapping(restResult)(restConcrete)

// The type a constructor's curried scheme body finally produces, read without building anything.
let recursive heapConstructorResultType (body: SemanticType) =
    match body with
        | SemFunction(_parameter, result, None) -> heapConstructorResultType(result)
        | _ -> body

let typeGraphConstructorBuilds (symbolId: Int) (typeName: Str) (body: SemanticType) =
    match heapConstructorResultType(body) with
        | SemNamed(candidateId, candidateName, _arguments) -> candidateId == symbolId && candidateName == typeName
        | _ -> false

// Every constructor of the named type identified by symbolId, in declaration order, paired with
// its own fields substituted for this use site's concrete type arguments. The scan covers every
// constructor the environment knows, so one that builds some other type is rejected on its result
// type alone, before its field list is peeled: each query otherwise allocates in proportion to
// the whole program's constructors rather than to the one type asked about.
let recursive heapNamedTypeSubstitutedFields (symbolId: Int) (typeName: Str) (arguments: List(SemanticType)) (constructors: List(ConstructorInferenceDefinition)) =
    match constructors with
        | [] -> []
        | ConstructorInferenceDefinition { name = constructorName, scheme = TypeScheme { body = body } } :: rest ->
            if typeGraphConstructorBuilds(symbolId)(typeName)(body)
            then
                match heapConstructorFieldShape(body)([]) with
                    | (fieldTypes, SemNamed(_candidateId, _candidateName, resultArguments)) ->
                        let mapping = heapParameterMapping(resultArguments)(arguments)
                        in
                            let substitutedFields =
                                map(applySubstitution(mapping))(fieldTypes)
                            in (constructorName, substitutedFields) :: heapNamedTypeSubstitutedFields(symbolId)(typeName)(arguments)(rest)
                    | _ -> heapNamedTypeSubstitutedFields(symbolId)(typeName)(arguments)(rest)
            else heapNamedTypeSubstitutedFields(symbolId)(typeName)(arguments)(rest)

type HeapGroupKey =
    | symbolId: Int
    | typeName: Str

type HeapGroupIndex =
    | groups: MapTree(Int, MapTree(Str, ConstructorFieldGroup))
    | reversedOrder: List(HeapGroupKey)

let heapGroupsForId (symbolId: Int) (index: HeapGroupIndex) =
    match Ashes.Collection.Map.getInt(symbolId)(index.groups) with
        | Some(groups) -> groups
        | None -> Ashes.Collection.Map.empty

let heapIndexGroup (symbolId: Int) (typeName: Str) (constructorName: Str) (fieldTypes: List(SemanticType)) (resultArguments: List(SemanticType)) (index: HeapGroupIndex) =
    (let byName = heapGroupsForId(symbolId)(index)
    in
        match Ashes.Collection.Map.getStr(typeName)(byName) with
            | None ->
                let group = ConstructorFieldGroup(groupSymbolId = symbolId, groupTypeName = typeName, groupIsGeneric = length(resultArguments) > 0, groupFields = [(constructorName, fieldTypes)], groupGenericFields = [(constructorName, fieldTypes, resultArguments)])
                in
                    index with groups = Ashes.Collection.Map.setInt(symbolId)(Ashes.Collection.Map.setStr(typeName)(group)(byName))(index.groups), reversedOrder = HeapGroupKey(symbolId = symbolId, typeName = typeName) :: index.reversedOrder
            | Some(group) ->
                let updated = group with groupIsGeneric = group.groupIsGeneric || length(resultArguments) > 0, groupFields = (constructorName, fieldTypes) :: group.groupFields, groupGenericFields = (constructorName, fieldTypes, resultArguments) :: group.groupGenericFields
                in
                    index with groups = Ashes.Collection.Map.setInt(symbolId)(Ashes.Collection.Map.setStr(typeName)(updated)(byName))(index.groups))

let recursive heapCollectGroups (constructors: List(ConstructorInferenceDefinition)) (index: HeapGroupIndex) =
    match constructors with
        | [] -> index
        | ConstructorInferenceDefinition { name = constructorName, scheme = TypeScheme { body = body } } :: rest ->
            match heapConstructorFieldShape(body)([]) with
                | (fieldTypes, SemNamed(symbolId, typeName, resultArguments)) ->
                    index
                    |> heapIndexGroup(symbolId)(typeName)(constructorName)(fieldTypes)(resultArguments)
                    |> heapCollectGroups(rest)
                | _ -> heapCollectGroups(rest)(index)

let heapGroupInDeclarationOrder (group: ConstructorFieldGroup) =
    match group with
        | ConstructorFieldGroup { groupSymbolId = symbolId, groupTypeName = typeName, groupIsGeneric = isGeneric, groupFields = fields, groupGenericFields = genericFields } ->
            ConstructorFieldGroup(
                groupSymbolId = symbolId,
                groupTypeName = typeName,
                groupIsGeneric = isGeneric,
                groupFields = reverse(fields),
                groupGenericFields = reverse(genericFields)
            )

let recursive heapGroupsInOrder (keys: List(HeapGroupKey)) (index: HeapGroupIndex) =
    match keys with
        | [] -> []
        | HeapGroupKey { symbolId = symbolId, typeName = typeName } :: rest ->
            match index
            |> heapGroupsForId(symbolId)
            |> Ashes.Collection.Map.getStr(typeName) with
                | Some(group) -> heapGroupInDeclarationOrder(group) :: heapGroupsInOrder(rest)(index)
                | None -> heapGroupsInOrder(rest)(index)

// Local indexed accumulation avoids rebuilding every preceding group for each constructor.
// The returned list retains first-seen group order and declaration order within each group.
let typeGraphConstructorFieldGroups (constructors: List(ConstructorInferenceDefinition)) =
    (let index = heapCollectGroups(constructors)(HeapGroupIndex(groups = Ashes.Collection.Map.empty, reversedOrder = []))
    in
        heapGroupsInOrder(reverse(index.reversedOrder))(index))

let recursive heapFindGroup (symbolId: Int) (typeName: Str) (groups: List(ConstructorFieldGroup)) =
    match groups with
        | [] -> None
        | (ConstructorFieldGroup { groupSymbolId = candidateId, groupTypeName = candidateName } as group) :: rest ->
            if candidateId == symbolId && candidateName == typeName
            then Some(group)
            else heapFindGroup(symbolId)(typeName)(rest)

// Preserve the first matching group, including when incremental declaration batches repeat a key.
let recursive heapIndexExistingGroups (groups: List(ConstructorFieldGroup)) (index: HeapConstructorLookupIndex) =
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
                in heapIndexExistingGroups(rest)(next)

let recursive heapIndexRecordKinds (constructors: List(ConstructorInferenceDefinition)) (index: HeapConstructorLookupIndex) =
    match constructors with
        | [] -> index
        | ConstructorInferenceDefinition { scheme = TypeScheme { body = body }, fieldNames = fieldNames } :: rest ->
            match heapConstructorResultType(body) with
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
                        in heapIndexRecordKinds(rest)(next)
                | _ -> heapIndexRecordKinds(rest)(index)

// Production environments retain the lookup maps alongside the ordered lists. Extending an
// already prepared environment indexes only the new declaration batch.
let typeGraphExtendConstructorEnvironment (environment: TypeEnvironment) (definitions: List(ConstructorInferenceDefinition)) =
    (let previous =
        match environment.heapConstructorIndex with
            | Some(index) -> index
            | None ->
                HeapConstructorLookupIndex(groups = Ashes.Collection.Map.empty, records = Ashes.Collection.Map.empty)
                |> heapIndexExistingGroups(environment.constructorFieldGroups)
                |> heapIndexRecordKinds(environment.constructors)
    in
        let groups = typeGraphConstructorFieldGroups(definitions)
        in
            let index =
                previous
                |> heapIndexExistingGroups(groups)
                |> heapIndexRecordKinds(definitions)
            in environment with constructors = append(environment.constructors)(definitions), constructorFieldGroups = append(environment.constructorFieldGroups)(groups), heapConstructorIndex = Some(index))

let heapEnvironmentGroup (symbolId: Int) (name: Str) (environment: TypeEnvironment) =
    match environment.heapConstructorIndex with
        | None -> heapFindGroup(symbolId)(name)(environment.constructorFieldGroups)
        | Some(HeapConstructorLookupIndex { groups = groups }) ->
            match Ashes.Collection.Map.getInt(symbolId)(groups) with
                | None -> None
                | Some(byName) -> Ashes.Collection.Map.getStr(name)(byName)

let recursive heapSubstituteGroupFields (arguments: List(SemanticType)) (genericFields: List((Str, List(SemanticType), List(SemanticType)))) =
    match genericFields with
        | [] -> []
        | (constructorName, fieldTypes, resultArguments) :: rest ->
            let mapping = heapParameterMapping(resultArguments)(arguments)
            in
                (constructorName, map(applySubstitution(mapping))(fieldTypes)) :: heapSubstituteGroupFields(arguments)(rest)

// The named type's constructors with their fields instantiated at this use site, or an empty list
// for a type whose constructors are not visible in the environment (a compiler-provided handle).
// An environment that indexes its constructors answers from the type's own group, and for a type
// with no parameters the group's field lists are the answer as they stand; one that does not is
// scanned whole.
let typeGraphNamedTypeFields (symbolId: Int) (name: Str) (arguments: List(SemanticType)) (environment: TypeEnvironment) =
    match environment.constructorFieldGroups with
        | [] ->
            environment
            |> typeGraphEnvironmentConstructors
            |> heapNamedTypeSubstitutedFields(symbolId)(name)(arguments)
        | _groups ->
            match heapEnvironmentGroup(symbolId)(name)(environment) with
                | None -> []
                | Some(ConstructorFieldGroup { groupIsGeneric = false, groupFields = fields }) -> fields
                | Some(ConstructorFieldGroup { groupGenericFields = genericFields }) -> heapSubstituteGroupFields(arguments)(genericFields)

let typeGraphNamedTypeConstructors (named: SemanticType) (environment: TypeEnvironment) =
    match named with
        | SemNamed(symbolId, name, arguments) -> typeGraphNamedTypeFields(symbolId)(name)(arguments)(environment)
        | _ -> []

// Whether the named type's first constructor declares record field names.
let recursive typeGraphNamedTypeHasFieldNames (symbolId: Int) (typeName: Str) (constructors: List(ConstructorInferenceDefinition)) =
    match constructors with
        | [] -> false
        | ConstructorInferenceDefinition { scheme = TypeScheme { body = body }, fieldNames = fieldNames } :: rest ->
            if typeGraphConstructorBuilds(symbolId)(typeName)(body)
            then length(fieldNames) >= 1
            else typeGraphNamedTypeHasFieldNames(symbolId)(typeName)(rest)

let typeGraphNamedTypeIsRecord (named: SemanticType) (environment: TypeEnvironment) =
    match named with
        | SemNamed(symbolId, name, _arguments) ->
            match environment.heapConstructorIndex with
                | None ->
                    environment
                    |> typeGraphEnvironmentConstructors
                    |> typeGraphNamedTypeHasFieldNames(symbolId)(name)
                | Some(HeapConstructorLookupIndex { records = records }) ->
                    match Ashes.Collection.Map.getInt(symbolId)(records) with
                        | None -> false
                        | Some(byName) ->
                            match Ashes.Collection.Map.getStr(name)(byName) with
                                | None -> false
                                | Some(isRecord) -> isRecord
        | _ -> false

// The different edge sets intentionally remain separate. Resource reachability does not inspect
// phantom generic arguments; unresolved reachability does. Argument containment additionally
// inspects pointers and capability arguments, and decides independently when to expand fields.
type TypeGraphNode =
    | nodeId: Int
    | sourceType: SemanticType
    | resolvedType: SemanticType
    | sourceMatchesResolved: Bool
    | constructorGroups: List((Str, List(SemanticType)))
    | sourceConstructorGroups: List((Str, List(SemanticType)))
    | isRecord: Bool
    | sourceIsRecord: Bool
    | fields: List(SemanticType)
    | resourceChildren: List(SemanticType)
    | unresolvedChildren: List(SemanticType)
    | argumentComponents: List(SemanticType)
    | directResource: Bool
    | directUnresolved: Bool
    deriving {Eq, Show}

type TypeGraphFactKind =
    | ResourceFact
    | UnresolvedFact

type TypeGraphEntry =
    | node: TypeGraphNode
    | resourceFact: Maybe(Bool)
    | unresolvedFact: Maybe(Bool)

// A complete structural fingerprint selects an integer AVL bucket; exact type equality resolves
// collisions. Key computation is linear in type size, tree lookup logarithmic in distinct hashes.
// The comparator remains an independent full-identity oracle for differential tests.
type TypeGraphIndex =
    | environment: TypeEnvironment
    | nodes: MapTree(Int, List(TypeGraphEntry))
    | nodeCount: Int

let emptyTypeGraphIndex (environment: TypeEnvironment) = TypeGraphIndex(environment = environment, nodes = Ashes.Collection.Map.empty, nodeCount = 0)

let typeVariant (semanticType: SemanticType) =
    match semanticType with
        | SemInt -> 0
        | SemUInt(_bits) -> 1
        | SemFloat -> 2
        | SemBigInt -> 3
        | SemString -> 4
        | SemRune -> 5
        | SemBytes -> 6
        | SemBool -> 7
        | SemNever -> 8
        | SemList(_element) -> 9
        | SemTuple(_elements) -> 10
        | SemFunction(_parameter, _result, _row) -> 11
        | SemVariable(_id) -> 12
        | SemCapability(_name, _arguments) -> 13
        | SemRow(_elements, _tail) -> 14
        | SemNamed(_id, _name, _arguments) -> 15
        | SemParameter(_id, _name) -> 16
        | SemOpaque(_name) -> 17
        | SemPointer(_element) -> 18

let compareInt left right =
    if left < right
    then -1
    else
        if left > right
        then 1
        else 0

let recursive compareTypeLists left right =
    match (left, right) with
        | ([], []) -> 0
        | ([], _ :: _) -> -1
        | (_ :: _, []) -> 1
        | (leftHead :: leftTail, rightHead :: rightTail) ->
            let order = compareTypeGraphKeys(leftHead)(rightHead)
            in
                if order == 0
                then compareTypeLists(leftTail)(rightTail)
                else order
and compareOptionalTypes left right =
    match (left, right) with
        | (None, None) -> 0
        | (None, Some(_)) -> -1
        | (Some(_), None) -> 1
        | (Some(leftType), Some(rightType)) -> compareTypeGraphKeys(leftType)(rightType)
and compareTypeGraphKeys (left: SemanticType) (right: SemanticType) =
    (let variantOrder =
        right
        |> typeVariant
        |> compareInt(typeVariant(left))
    in
        if variantOrder != 0
        then variantOrder
        else
            match (left, right) with
                | (SemUInt(a), SemUInt(b)) -> compareInt(a)(b)
                | (SemVariable(a), SemVariable(b)) -> compareInt(a)(b)
                | (SemList(a), SemList(b)) -> compareTypeGraphKeys(a)(b)
                | (SemPointer(a), SemPointer(b)) -> compareTypeGraphKeys(a)(b)
                | (SemTuple(a), SemTuple(b)) -> compareTypeLists(a)(b)
                | (SemOpaque(a), SemOpaque(b)) -> Ashes.Text.compare(a)(b)
                | (SemNamed(leftId, leftName, leftArguments), SemNamed(rightId, rightName, rightArguments)) ->
                    let idOrder = compareInt(leftId)(rightId)
                    in
                        if idOrder != 0
                        then idOrder
                        else
                            let nameOrder = Ashes.Text.compare(leftName)(rightName)
                            in
                                if nameOrder != 0
                                then nameOrder
                                else compareTypeLists(leftArguments)(rightArguments)
                | (SemParameter(leftId, leftName), SemParameter(rightId, rightName)) ->
                    let idOrder = compareInt(leftId)(rightId)
                    in
                        if idOrder != 0
                        then idOrder
                        else Ashes.Text.compare(leftName)(rightName)
                | (SemCapability(leftName, leftArguments), SemCapability(rightName, rightArguments)) ->
                    let nameOrder = Ashes.Text.compare(leftName)(rightName)
                    in
                        if nameOrder != 0
                        then nameOrder
                        else compareTypeLists(leftArguments)(rightArguments)
                | (SemFunction(leftParameter, leftResult, leftRow), SemFunction(rightParameter, rightResult, rightRow)) ->
                    let parameterOrder = compareTypeGraphKeys(leftParameter)(rightParameter)
                    in
                        if parameterOrder != 0
                        then parameterOrder
                        else
                            let resultOrder = compareTypeGraphKeys(leftResult)(rightResult)
                            in
                                if resultOrder != 0
                                then resultOrder
                                else compareOptionalTypes(leftRow)(rightRow)
                | (SemRow(leftElements, leftTail), SemRow(rightElements, rightTail)) ->
                    let elementsOrder = compareTypeLists(leftElements)(rightElements)
                    in
                        if elementsOrder != 0
                        then elementsOrder
                        else compareOptionalTypes(leftTail)(rightTail)
                | _ -> 0)

let combineHash left right = (left * 16777619 + right) % 2147483647

let stringHash name =
    Ashes.Byte.hash(Ashes.Byte.fromText(name)) % 2147483647

let recursive hashTypeList seed values =
    match values with
        | [] -> combineHash(seed)(0)
        | head :: rest ->
            hashTypeList(head
            |> typeGraphFingerprint
            |> combineHash(combineHash(seed)(1)))(rest)
and hashOptionalType value =
    match value with
        | None -> 0
        | Some(semanticType) ->
            semanticType
            |> typeGraphFingerprint
            |> combineHash(1)
and typeGraphFingerprint semanticType =
    (let tag = typeVariant(semanticType) + 1
    in
        match semanticType with
            | SemUInt(bits) -> combineHash(tag)(bits)
            | SemVariable(id) -> combineHash(tag)(id)
            | SemList(element) ->
                element
                |> typeGraphFingerprint
                |> combineHash(tag)
            | SemPointer(element) ->
                element
                |> typeGraphFingerprint
                |> combineHash(tag)
            | SemTuple(elements) -> hashTypeList(tag)(elements)
            | SemOpaque(name) ->
                name
                |> stringHash
                |> combineHash(tag)
            | SemNamed(id, name, arguments) ->
                hashTypeList(name
                |> stringHash
                |> combineHash(combineHash(tag)(id)))(arguments)
            | SemParameter(id, name) ->
                name
                |> stringHash
                |> combineHash(combineHash(tag)(id))
            | SemCapability(name, arguments) ->
                hashTypeList(name
                |> stringHash
                |> combineHash(tag))(arguments)
            | SemFunction(parameter, result, row) ->
                row
                |> hashOptionalType
                |> combineHash(result
                |> typeGraphFingerprint
                |> combineHash(parameter
                |> typeGraphFingerprint
                |> combineHash(tag)))
            | SemRow(elements, tail) ->
                tail
                |> hashOptionalType
                |> combineHash(hashTypeList(tag)(elements))
            | _ -> tag)

let bucket hash (index: TypeGraphIndex) =
    match Ashes.Collection.Map.getInt(hash)(index.nodes) with
        | None -> []
        | Some(entries) -> entries

let recursive findInBucket (semanticType: SemanticType) (entries: List(TypeGraphEntry)) =
    match entries with
        | [] -> None
        | (TypeGraphEntry { node = node } as entry) :: rest ->
            if node.sourceType == semanticType
            then Some(entry)
            else findInBucket(semanticType)(rest)

let recursive putInBucket (semanticType: SemanticType) (entry: TypeGraphEntry) (entries: List(TypeGraphEntry)) =
    match entries with
        | [] -> [entry]
        | (TypeGraphEntry { node = previousNode } as previous) :: rest ->
            if previousNode.sourceType == semanticType
            then entry :: rest
            else previous :: putInBucket(semanticType)(entry)(rest)

let findNodeWithHash hash semanticType (index: TypeGraphIndex) =
    index
    |> bucket(hash)
    |> findInBucket(semanticType)

let findNode semanticType index =
    findNodeWithHash(typeGraphFingerprint(semanticType))(semanticType)(index)

// Read-only consumers may reuse a completed view without discovering a node whose
// updated index they cannot retain. A miss never builds a node or inspects children.
let typeGraphLookupNode (semanticType: SemanticType) (index: TypeGraphIndex) =
    match findNode(semanticType)(index) with
        | Some(entry) -> Some(entry.node)
        | None -> None

let setNodeWithHash hash semanticType entry (index: TypeGraphIndex) =
    index with nodes = Ashes.Collection.Map.setInt(hash)(index
    |> bucket(hash)
    |> putInBucket(semanticType)(entry))(index.nodes)

let recursive flattenGroups grouped =
    match grouped with
        | [] -> []
        | (_name, fields) :: rest ->
            rest
            |> flattenGroups
            |> append(fields)

let graphArgumentComponents semanticType =
    match semanticType with
        | SemList(element) -> [element]
        | SemPointer(element) -> [element]
        | SemTuple(elements) -> elements
        | SemNamed(_id, _name, arguments) -> arguments
        | SemCapability(_name, arguments) -> arguments
        | _ -> []

let graphResourceChildren resolved fields =
    match resolved with
        | SemNamed(_id, _name, _arguments) -> fields
        | SemList(element) -> [element]
        | SemTuple(elements) -> elements
        | _ -> []

let graphDirectResource resolved environment =
    match resolved with
        | SemOpaque(name) ->
            environment
            |> inferenceTypeResolutionContext
            |> externalTypeHasDestructor(name)
        | SemNamed(_id, name, _arguments) -> name == "Socket" || name == "TlsSocket" || name == "Process" || name == "FileHandle" || name == "JoinHandle"
        | _ -> false

let graphDirectUnresolved resolved =
    match resolved with
        | SemVariable(_id) -> true
        | SemParameter(_id, _name) -> true
        | _ -> false

let buildNode nodeId semanticType environment =
    (let resolved =
        environment
        |> inferenceTypeResolutionContext
        |> semanticRuntimeRepresentation(semanticType)
    in
        let sourceMatchesResolved = semanticType == resolved
        in
            let isRecord = typeGraphNamedTypeIsRecord(resolved)(environment)
            in
                let groups = typeGraphNamedTypeConstructors(resolved)(environment)
                in
                    let fields = flattenGroups(groups)
                    in
                        let resourceChildren = graphResourceChildren(resolved)(fields)
                        in
                            TypeGraphNode(
                                nodeId = nodeId,
                                sourceType = semanticType,
                                resolvedType = resolved,
                                sourceMatchesResolved = sourceMatchesResolved,
                                constructorGroups = groups,
                                sourceConstructorGroups = if sourceMatchesResolved
                                then groups
                                else typeGraphNamedTypeConstructors(semanticType)(environment),
                                isRecord = isRecord,
                                sourceIsRecord = if sourceMatchesResolved
                                then isRecord
                                else typeGraphNamedTypeIsRecord(semanticType)(environment),
                                fields = fields,
                                resourceChildren = resourceChildren,
                                unresolvedChildren = match resolved with
                                    | SemNamed(_id, _name, arguments) -> append(arguments)(fields)
                                    | _ -> resourceChildren,
                                argumentComponents = graphArgumentComponents(resolved),
                                directResource = graphDirectResource(resolved)(environment),
                                directUnresolved = graphDirectUnresolved(resolved)
                            ))

let typeGraphNode (semanticType: SemanticType) (index: TypeGraphIndex) =
    (let hash = typeGraphFingerprint(semanticType)
    in
        match findNodeWithHash(hash)(semanticType)(index) with
            | Some(entry) -> (entry.node, index)
            | None ->
                let node = buildNode(index.nodeCount)(semanticType)(index.environment)
                in
                    let entry = TypeGraphEntry(node = node, resourceFact = None, unresolvedFact = None)
                    in
                        let updated = setNodeWithHash(hash)(semanticType)(entry)(index)
                        in (node, (updated with nodeCount = updated.nodeCount + 1)))

let typeGraphRootFact kind semanticType index =
    match findNode(semanticType)(index) with
        | None -> None
        | Some(entry) ->
            match kind with
                | ResourceFact -> entry.resourceFact
                | UnresolvedFact -> entry.unresolvedFact

let setEntryFact kind answer (entry: TypeGraphEntry) =
    match kind with
        | ResourceFact -> entry with resourceFact = Some(answer)
        | UnresolvedFact -> entry with unresolvedFact = Some(answer)

// A whole-root result can be reused. A path-dependent result must not be published here.
// In particular, an interrupted/positive search cannot publish negatives for visited nodes.
let completeTypeGraphRootFact kind semanticType answer index =
    (let hash = typeGraphFingerprint(semanticType)
    in
        match findNodeWithHash(hash)(semanticType)(index) with
            | Some(entry) ->
                setNodeWithHash(hash)(semanticType)(setEntryFact(kind)(answer)(entry))(index)
            | None ->
                let node = buildNode(index.nodeCount)(semanticType)(index.environment)
                in
                    let entry = TypeGraphEntry(node = node, resourceFact = None, unresolvedFact = None)
                    in
                        let updated =
                            setNodeWithHash(hash)(semanticType)(setEntryFact(kind)(answer)(entry))(index)
                        in updated with nodeCount = updated.nodeCount + 1)

// Only pass roots from an exhausted negative closure search, never a path-cut fallback's visits.
let recursive completeTypeGraphNegativeRoots kind roots index =
    match roots with
        | [] -> index
        | semanticType :: rest ->
            index
            |> completeTypeGraphRootFact(kind)(semanticType)(false)
            |> completeTypeGraphNegativeRoots(kind)(rest)
