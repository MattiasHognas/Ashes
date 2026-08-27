// Classifies a resolved SemanticType's heap layout for ownership and Perceus drop-insertion
// purposes: whether the value transitively contains a declared resource type or an unresolved
// type variable, and — for list, tuple, and named-ADT shapes — the per-child type and drop
// operation kind the drop machinery will need. A zero-cost newtype is erased to its runtime
// representation before classification, at every level, not only at the top.
//
// Invariants:
// - A scalar type (Int, UInt, Float, Rune, Bool) never needs a drop: it has no heap payload.
// - Recursive ADT cycles are guarded by symbol id; the path back to an already-visited symbol
//   reports neither "resource" nor "unresolved" — only a fact reachable via a fresh, non-cyclic
//   path counts, matching the stage-0 reference classifier's own cycle-guard convention.
// - A named type's constructor field types are instantiated against its own concrete type
//   arguments (positionally, via the constructor's generalized result type), never returned raw
//   with generic type-parameter placeholders still attached.

import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.TypeInference
import Ashes.Collection.List.map
import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
export (
    type HeapChildDropKind(..),
    type HeapLayoutChild(..),
    type HeapLayoutFacts(..),
    value canArenaResetLayout,
    value classifyHeapLayout,
)

type HeapChildDropKind =
    | NoChildDrop
    | DropString
    | DropBytes
    | DropBigInt
    | DropList
    | DropTuple
    | DropAdt
    | UnsupportedChildDrop
    deriving {Eq, Show}

type HeapLayoutChild =
    | constructorName: Maybe(Str)
    | fieldIndex: Int
    | childType: SemanticType
    | dropKind: HeapChildDropKind
    deriving {Eq, Show}

type HeapLayoutFacts =
    | containsResource: Bool
    | containsUnresolvedType: Bool
    | containsOwnedChild: Bool
    | children: List(HeapLayoutChild)
    deriving {Eq, Show}

let canArenaResetLayout (semanticType: SemanticType) =
    match semanticType with
        | SemInt -> true
        | SemUInt(_bits) -> true
        | SemFloat -> true
        | SemRune -> true
        | SemBool -> true
        | _ -> false

let resolveLayoutType (semanticType: SemanticType) (environment: TypeEnvironment) = semanticRuntimeRepresentation(semanticType)(inferenceTypeResolutionContext(environment))

let recursive containsIntId (target: Int) (ids: List(Int)) =
    match ids with
        | [] -> false
        | id :: rest ->
            if id == target
            then true
            else containsIntId(target)(rest)

let heapEnvironmentConstructors (environment: TypeEnvironment) =
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

// Every constructor of the named type identified by symbolId, in declaration order, paired with
// its own fields substituted for this use site's concrete type arguments.
let recursive heapNamedTypeSubstitutedFields (symbolId: Int) (arguments: List(SemanticType)) (constructors: List(ConstructorInferenceDefinition)) =
    match constructors with
        | [] -> []
        | ConstructorInferenceDefinition { name = constructorName, scheme = TypeScheme { body = body } } :: rest ->
            match heapConstructorFieldShape(body)([]) with
                | (fieldTypes, SemNamed(candidateId, _candidateName, resultArguments)) ->
                    if candidateId == symbolId
                    then
                        let mapping = heapParameterMapping(resultArguments)(arguments)
                        in
                            let substitutedFields = map(applySubstitution(mapping))(fieldTypes)
                            in (constructorName, substitutedFields) :: heapNamedTypeSubstitutedFields(symbolId)(arguments)(rest)
                    else heapNamedTypeSubstitutedFields(symbolId)(arguments)(rest)
                | _ -> heapNamedTypeSubstitutedFields(symbolId)(arguments)(rest)

let recursive heapExternalTypeHasDestructor (name: Str) (context: TypeResolutionContext) =
    match context with
        | TypeResolutionContext { definitions = definitions } -> heapFindExternalDestructor(name)(definitions)
and heapFindExternalDestructor (name: Str) (definitions: List(TypeDefinition)) =
    match definitions with
        | [] -> false
        | ExternalTypeDefinition(candidateName, destructor) :: rest ->
            if candidateName == name
            then
                match destructor with
                    | Some(_destructorName) -> true
                    | None -> false
            else heapFindExternalDestructor(name)(rest)
        | _other :: rest -> heapFindExternalDestructor(name)(rest)

let heapDropKind (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let resolved = resolveLayoutType(semanticType)(environment)
    in
        if canArenaResetLayout(resolved)
        then NoChildDrop
        else
            match resolved with
                | SemString -> DropString
                | SemBytes -> DropBytes
                | SemBigInt -> DropBigInt
                | SemList(_element) -> DropList
                | SemTuple(_elements) -> DropTuple
                | SemNamed(_symbolId, _name, _arguments) -> DropAdt
                | _ -> UnsupportedChildDrop)

let recursive heapChildrenOf (fields: List((Maybe(Str), SemanticType))) (index: Int) (environment: TypeEnvironment) =
    match fields with
        | [] -> []
        | (constructorName, fieldType) :: rest ->
            HeapLayoutChild(
                constructorName = constructorName,
                fieldIndex = index,
                childType = fieldType,
                dropKind = heapDropKind(fieldType)(environment)
            ) :: heapChildrenOf(rest)(index + 1)(environment)

let recursive heapNamedTypeChildren (grouped: List((Str, List(SemanticType)))) (environment: TypeEnvironment) =
    match grouped with
        | [] -> []
        | (constructorName, fieldTypes) :: rest ->
            append(heapChildrenOf(map(given (fieldType) -> (Some(constructorName), fieldType))(fieldTypes))(0)(environment))(heapNamedTypeChildren(rest)(environment))

let heapLayoutChildren (resolved: SemanticType) (environment: TypeEnvironment) =
    match resolved with
        | SemList(element) -> heapChildrenOf([(None, element), (None, resolved)])(0)(environment)
        | SemTuple(elements) ->
            heapChildrenOf(map(given (element) -> (None, element))(elements))(0)(environment)
        | SemNamed(symbolId, _name, arguments) -> heapNamedTypeChildren(heapNamedTypeSubstitutedFields(symbolId)(arguments)(heapEnvironmentConstructors(environment)))(environment)
        | _ -> []

let recursive heapAnyResource (types: List(SemanticType)) (environment: TypeEnvironment) (path: List(Int)) =
    match types with
        | [] -> false
        | head :: rest ->
            if heapContainsResource(head)(environment)(path)
            then true
            else heapAnyResource(rest)(environment)(path)
and heapAnyGroupedResource (grouped: List((Str, List(SemanticType)))) (environment: TypeEnvironment) (path: List(Int)) =
    match grouped with
        | [] -> false
        | (_constructorName, fieldTypes) :: rest ->
            if heapAnyResource(fieldTypes)(environment)(path)
            then true
            else heapAnyGroupedResource(rest)(environment)(path)
and heapContainsResource (semanticType: SemanticType) (environment: TypeEnvironment) (path: List(Int)) =
    (let resolved = resolveLayoutType(semanticType)(environment)
    in
        match resolved with
            | SemOpaque(name) -> heapExternalTypeHasDestructor(name)(inferenceTypeResolutionContext(environment))
            | SemNamed(symbolId, _name, arguments) ->
                if containsIntId(symbolId)(path)
                then false
                else heapAnyGroupedResource(heapNamedTypeSubstitutedFields(symbolId)(arguments)(heapEnvironmentConstructors(environment)))(environment)(symbolId :: path)
            | SemTuple(elements) -> heapAnyResource(elements)(environment)(path)
            | SemList(element) -> heapContainsResource(element)(environment)(path)
            | _ -> false)

let recursive heapAnyUnresolved (types: List(SemanticType)) (environment: TypeEnvironment) (path: List(Int)) =
    match types with
        | [] -> false
        | head :: rest ->
            if heapContainsUnresolvedType(head)(environment)(path)
            then true
            else heapAnyUnresolved(rest)(environment)(path)
and heapAnyGroupedUnresolved (grouped: List((Str, List(SemanticType)))) (environment: TypeEnvironment) (path: List(Int)) =
    match grouped with
        | [] -> false
        | (_constructorName, fieldTypes) :: rest ->
            if heapAnyUnresolved(fieldTypes)(environment)(path)
            then true
            else heapAnyGroupedUnresolved(rest)(environment)(path)
and heapContainsUnresolvedType (semanticType: SemanticType) (environment: TypeEnvironment) (path: List(Int)) =
    (let resolved = resolveLayoutType(semanticType)(environment)
    in
        match resolved with
            | SemVariable(_id) -> true
            | SemParameter(_id, _name) -> true
            | SemList(element) -> heapContainsUnresolvedType(element)(environment)(path)
            | SemTuple(elements) -> heapAnyUnresolved(elements)(environment)(path)
            | SemNamed(symbolId, _name, arguments) ->
                if containsIntId(symbolId)(path)
                then false
                else
                    let extendedPath = symbolId :: path
                    in
                        if heapAnyUnresolved(arguments)(environment)(extendedPath)
                        then true
                        else heapAnyGroupedUnresolved(heapNamedTypeSubstitutedFields(symbolId)(arguments)(heapEnvironmentConstructors(environment)))(environment)(extendedPath)
            | _ -> false)

let recursive heapAnyOwnedChild (children: List(HeapLayoutChild)) =
    match children with
        | [] -> false
        | HeapLayoutChild { dropKind = dropKind } :: rest ->
            if dropKind == NoChildDrop
            then heapAnyOwnedChild(rest)
            else true

let classifyHeapLayout (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let resolved = resolveLayoutType(semanticType)(environment)
    in
        let children = heapLayoutChildren(resolved)(environment)
        in
            HeapLayoutFacts(
                containsResource = heapContainsResource(resolved)(environment)([]),
                containsUnresolvedType = heapContainsUnresolvedType(resolved)(environment)([]),
                containsOwnedChild = heapAnyOwnedChild(children),
                children = children
            ))
