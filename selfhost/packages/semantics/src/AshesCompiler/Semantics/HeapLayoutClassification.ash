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
import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.TypeInference
import Ashes.Collection.List.map
import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import Ashes.Collection.List.length
export (
    type HeapChildDropKind(..),
    type HeapStructuralCopyKind(..),
    type HeapLayoutChild(..),
    type HeapLayoutRejections(..),
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

// The structural copy operation a value graph supports: a scalar word, a single cell whose
// payload words are all scalars, a full structural walk, or none.
type HeapStructuralCopyKind =
    | NoStructuralCopy
    | InlineCopy
    | ShallowCopy
    | DeepCopy
    deriving {Eq, Show}

type HeapLayoutChild =
    | constructorName: Maybe(Str)
    | fieldIndex: Int
    | childType: SemanticType
    | dropKind: HeapChildDropKind
    | copyKind: HeapStructuralCopyKind
    deriving {Eq, Show}

// Stable reasons why a layout's runtime reuse capability is conservative.
type HeapLayoutRejections =
    | resourceOrBorrowedViewContainment: Bool
    | unsupportedChildDropLayout: Bool
    | unresolvedType: Bool
    | unsupportedOuterCellReuse: Bool
    deriving {Eq, Show}

// The named-ADT reuse layouts the runtime can manage for an outer cell.
type HeapRuntimeReuseSupport =
    | copyAdt: Bool
    | recordAdt: Bool
    | ownedChildAdt: Bool
    | tcoOwnedChildAdt: Bool
    | recursiveAdt: Bool
    deriving {Eq, Show}

type HeapLayoutFacts =
    | containsResource: Bool
    | containsUnresolvedType: Bool
    | containsOwnedChild: Bool
    | structuralCopy: HeapStructuralCopyKind
    | arenaDeepCopySupported: Bool
    | ownedChildrenDroppable: Bool
    | runtimeOuterCellReuseSupported: Bool
    | runtimeCopyAdtSupported: Bool
    | runtimeRecordAdtSupported: Bool
    | runtimeOwnedChildAdtSupported: Bool
    | runtimeTcoOwnedChildAdtSupported: Bool
    | runtimeTcoListElementSupported: Bool
    | children: List(HeapLayoutChild)
    | rejections: HeapLayoutRejections
    deriving {Eq, Show}

let canArenaResetLayout (semanticType: SemanticType) =
    match semanticType with
        | SemInt -> true
        | SemUInt(_bits) -> true
        | SemFloat -> true
        | SemRune -> true
        | SemBool -> true
        | _ -> false

let resolveLayoutType (semanticType: SemanticType) (environment: TypeEnvironment) =
    environment
    |> inferenceTypeResolutionContext
    |> semanticRuntimeRepresentation(semanticType)

let heapCanReset (semanticType: SemanticType) (environment: TypeEnvironment) =
    environment
    |> resolveLayoutType(semanticType)
    |> canArenaResetLayout

// Built-in handle types whose values own an operating-system resource.
let heapBuiltinResourceTypeName (name: Str) = name == "Socket" || name == "TlsSocket" || name == "Process" || name == "FileHandle" || name == "JoinHandle"

// Types the compiler provides itself rather than user source declares.
let heapBuiltinTypeName (name: Str) = name == "Unit" || name == "List" || name == "Maybe" || name == "Result" || name == "Task" || heapBuiltinResourceTypeName(name)

let recursive containsIntId (target: Int) (ids: List(Int)) =
    match ids with
        | [] -> false
        | id :: rest ->
            if id == target
            then true
            else containsIntId(target)(rest)

let recursive heapAllTypes predicate (types: List(SemanticType)) =
    match types with
        | [] -> true
        | head :: rest ->
            if predicate(head)
            then heapAllTypes(predicate)(rest)
            else false

let recursive heapAnyType predicate (types: List(SemanticType)) =
    match types with
        | [] -> false
        | head :: rest ->
            if predicate(head)
            then true
            else heapAnyType(predicate)(rest)

let recursive heapAllGroupedFields predicate (grouped: List((Str, List(SemanticType)))) =
    match grouped with
        | [] -> true
        | (_constructorName, fieldTypes) :: rest ->
            if heapAllTypes(predicate)(fieldTypes)
            then heapAllGroupedFields(predicate)(rest)
            else false

let recursive heapAnyGroupedField predicate (grouped: List((Str, List(SemanticType)))) =
    match grouped with
        | [] -> false
        | (_constructorName, fieldTypes) :: rest ->
            if heapAnyType(predicate)(fieldTypes)
            then true
            else heapAnyGroupedField(predicate)(rest)

let recursive heapSameArity (arity: Int) (grouped: List((Str, List(SemanticType)))) =
    match grouped with
        | [] -> true
        | (_constructorName, fieldTypes) :: rest ->
            if length(fieldTypes) == arity
            then heapSameArity(arity)(rest)
            else false

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
                            let substitutedFields =
                                map(applySubstitution(mapping))(fieldTypes)
                            in (constructorName, substitutedFields) :: heapNamedTypeSubstitutedFields(symbolId)(arguments)(rest)
                    else heapNamedTypeSubstitutedFields(symbolId)(arguments)(rest)
                | _ -> heapNamedTypeSubstitutedFields(symbolId)(arguments)(rest)

// The named type's constructors with their fields instantiated at this use site, or an empty list
// for a type whose constructors are not visible in the environment (a compiler-provided handle).
let heapNamedTypeConstructors (named: SemanticType) (environment: TypeEnvironment) =
    match named with
        | SemNamed(symbolId, _name, arguments) ->
            environment
            |> heapEnvironmentConstructors
            |> heapNamedTypeSubstitutedFields(symbolId)(arguments)
        | _ -> []

// Whether the named type's first constructor declares record field names.
let recursive heapNamedTypeHasFieldNames (symbolId: Int) (constructors: List(ConstructorInferenceDefinition)) =
    match constructors with
        | [] -> false
        | ConstructorInferenceDefinition { scheme = TypeScheme { body = body }, fieldNames = fieldNames } :: rest ->
            match heapConstructorFieldShape(body)([]) with
                | (_fieldTypes, SemNamed(candidateId, _candidateName, _resultArguments)) ->
                    if candidateId == symbolId
                    then length(fieldNames) >= 1
                    else heapNamedTypeHasFieldNames(symbolId)(rest)
                | _ -> heapNamedTypeHasFieldNames(symbolId)(rest)

let heapNamedTypeIsRecord (named: SemanticType) (environment: TypeEnvironment) =
    match named with
        | SemNamed(symbolId, _name, _arguments) ->
            environment
            |> heapEnvironmentConstructors
            |> heapNamedTypeHasFieldNames(symbolId)
        | _ -> false

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
            | SemOpaque(name) ->
                environment
                |> inferenceTypeResolutionContext
                |> heapExternalTypeHasDestructor(name)
            | SemNamed(symbolId, name, arguments) ->
                if heapBuiltinResourceTypeName(name)
                then true
                else
                    if containsIntId(symbolId)(path)
                    then false
                    else
                        heapAnyGroupedResource(environment
                        |> heapEnvironmentConstructors
                        |> heapNamedTypeSubstitutedFields(symbolId)(arguments))(environment)(symbolId :: path)
            | SemTuple(elements) -> heapAnyResource(elements)(environment)(path)
            | SemList(element) -> heapContainsResource(element)(environment)(path)
            | _ -> false)

let heapResourceBearing (semanticType: SemanticType) (environment: TypeEnvironment) = heapContainsResource(semanticType)(environment)([])

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
                        else
                            heapAnyGroupedUnresolved(environment
                            |> heapEnvironmentConstructors
                            |> heapNamedTypeSubstitutedFields(symbolId)(arguments))(environment)(extendedPath)
            | _ -> false)

// A named type the runtime may never manage as an ordinary reuse cell: a built-in resource handle
// or any aggregate that transitively owns one.
let heapAdtExcludedFromReuse (named: SemanticType) (environment: TypeEnvironment) =
    match named with
        | SemNamed(_symbolId, name, _arguments) -> heapBuiltinResourceTypeName(name) || heapResourceBearing(named)(environment)
        | _ -> true

// A named type declared by user source with no type parameters at this use site.
let heapMonomorphicUserAdt (named: SemanticType) =
    match named with
        | SemNamed(_symbolId, name, arguments) -> !heapBuiltinTypeName(name) && length(arguments) == 0
        | _ -> false

// Whether the arena copier can walk the whole graph: scalars and strings are leaves, lists and
// tuples recurse, and a named type recurses through every constructor field unless it owns a
// resource or the walk has already entered it.
let recursive heapArenaDeepCopyLayout (semanticType: SemanticType) (environment: TypeEnvironment) (path: List(Int)) =
    (let resolved = resolveLayoutType(semanticType)(environment)
    in
        if canArenaResetLayout(resolved)
        then true
        else
            match resolved with
                | SemString -> true
                | SemBytes -> true
                | SemList(element) -> heapArenaDeepCopyLayout(element)(environment)(path)
                | SemTuple(elements) ->
                    heapAllTypes(given (element) -> heapArenaDeepCopyLayout(element)(environment)(path))(elements)
                | SemNamed(_symbolId, _name, _arguments) -> heapArenaDeepCopyAdtLayout(resolved)(environment)(path)
                | _ -> false)
and heapArenaDeepCopyAdtLayout (named: SemanticType) (environment: TypeEnvironment) (path: List(Int)) =
    match named with
        | SemNamed(symbolId, _name, _arguments) ->
            if containsIntId(symbolId)(path) || heapAdtExcludedFromReuse(named)(environment)
            then false
            else
                match heapNamedTypeConstructors(named)(environment) with
                    | [] -> false
                    | grouped ->
                        heapAllGroupedFields(given (fieldType) -> heapArenaDeepCopyLayout(fieldType)(environment)(symbolId :: path))(grouped)
        | _ -> false

// A named type is shallow-copyable when every constructor has the same arity and every field is a
// scalar word; otherwise it is deep-copyable exactly when the arena copier can walk it.
let heapAdtStructuralCopyKind (named: SemanticType) (environment: TypeEnvironment) =
    (let grouped = heapNamedTypeConstructors(named)(environment)
    in
        match grouped with
            | [] -> NoStructuralCopy
            | (_firstName, firstFields) :: _rest ->
                if heapResourceBearing(named)(environment)
                then NoStructuralCopy
                else
                    if heapSameArity(length(firstFields))(grouped) && heapAllGroupedFields(given (fieldType) -> heapCanReset(fieldType)(environment))(grouped)
                    then ShallowCopy
                    else
                        if heapArenaDeepCopyAdtLayout(named)(environment)([])
                        then DeepCopy
                        else NoStructuralCopy)

let heapStructuralCopyKind (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let resolved = resolveLayoutType(semanticType)(environment)
    in
        if canArenaResetLayout(resolved)
        then InlineCopy
        else
            match resolved with
                | SemString -> ShallowCopy
                | SemBytes -> ShallowCopy
                | SemBigInt -> ShallowCopy
                | SemList(element) ->
                    if heapArenaDeepCopyLayout(element)(environment)([])
                    then DeepCopy
                    else NoStructuralCopy
                | SemTuple(_elements) ->
                    if heapArenaDeepCopyLayout(resolved)(environment)([])
                    then DeepCopy
                    else NoStructuralCopy
                | SemNamed(_symbolId, _name, _arguments) -> heapAdtStructuralCopyKind(resolved)(environment)
                | _ -> NoStructuralCopy)

// The field rule shared by every drop and reuse layout: a scalar, a string-like leaf, a list of
// scalars, or a tuple of such leaves is always acceptable, a named field is decided by the caller's
// own rule, and anything else (a closure, an opaque handle) is not.
let recursive heapDroppableLeafField (semanticType: SemanticType) (environment: TypeEnvironment) namedRule =
    (let resolved = resolveLayoutType(semanticType)(environment)
    in
        if canArenaResetLayout(resolved)
        then true
        else
            match resolved with
                | SemString -> true
                | SemBytes -> true
                | SemBigInt -> true
                | SemList(element) -> heapCanReset(element)(environment)
                | SemTuple(elements) ->
                    heapAllTypes(given (element) -> heapDroppableTupleElement(element)(environment))(elements)
                | SemNamed(_symbolId, _name, _arguments) -> namedRule(resolved)
                | _ -> false)
and heapDroppableTupleElement (semanticType: SemanticType) (environment: TypeEnvironment) =
    heapDroppableLeafField(semanticType)(environment)(given (_named) -> false)

// Whether the type-directed drop walker can release every owned child of a named type's graph; a
// cycle back into a type already on the path is droppable by construction.
let recursive heapCanDropAdtGraph (named: SemanticType) (environment: TypeEnvironment) (path: List(Int)) =
    match named with
        | SemNamed(symbolId, _name, _arguments) ->
            if heapResourceBearing(named)(environment)
            then false
            else
                if containsIntId(symbolId)(path)
                then true
                else
                    environment
                    |> heapNamedTypeConstructors(named)
                    |> heapAllGroupedFields(given (fieldType) ->
                        heapDroppableLeafField(fieldType)(environment)(given (child) -> heapCanDropAdtGraph(child)(environment)(symbolId :: path)))
        | _ -> false

let heapCanDropValueGraph (semanticType: SemanticType) (environment: TypeEnvironment) =
    heapDroppableLeafField(semanticType)(environment)(given (named) -> heapCanDropAdtGraph(named)(environment)([]))

// A copy ADT: every field of every constructor is a scalar word.
let heapRuntimeCopyAdtLayout (named: SemanticType) (environment: TypeEnvironment) =
    match heapNamedTypeConstructors(named)(environment) with
        | [] -> false
        | grouped ->
            if heapAdtExcludedFromReuse(named)(environment)
            then false
            else
                heapAllGroupedFields(given (fieldType) -> heapCanReset(fieldType)(environment))(grouped)

// A record ADT: a single named-field constructor whose fields are leaves or, recursively, other
// record ADTs not already on the path.
let recursive heapRuntimeRecordAdtLayout (named: SemanticType) (environment: TypeEnvironment) (path: List(Int)) =
    match named with
        | SemNamed(symbolId, _name, _arguments) ->
            match heapNamedTypeConstructors(named)(environment) with
                | (_constructorName, fieldTypes) :: [] ->
                    if !heapNamedTypeIsRecord(named)(environment) || heapAdtExcludedFromReuse(named)(environment) || containsIntId(symbolId)(path)
                    then false
                    else
                        heapAllTypes(given (fieldType) ->
                            heapDroppableLeafField(fieldType)(environment)(given (child) -> heapRuntimeRecordAdtLayout(child)(environment)(symbolId :: path)))(fieldTypes)
                | _ -> false
        | _ -> false

// A TCO owned-child field: a scalar word or a list of scalar words.
let heapTcoOwnedChildField (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let resolved = resolveLayoutType(semanticType)(environment)
    in
        if canArenaResetLayout(resolved)
        then true
        else
            match resolved with
                | SemList(element) -> heapCanReset(element)(environment)
                | _ -> false)

let heapOwnsHeapField (semanticType: SemanticType) (environment: TypeEnvironment) = !heapCanReset(semanticType)(environment)

// An owned-child ADT: a monomorphic user type with at least two constructors and at least one
// heap-owning field, every such field being a leaf, a record ADT, or a TCO owned-child ADT not
// already on the path. A single unnamed-field constructor is instead the TCO owned-child layout:
// every heap-owning field a list of scalars, with at least one such field.
let recursive heapRuntimeOwnedChildAdtLayout (named: SemanticType) (environment: TypeEnvironment) (path: List(Int)) =
    match named with
        | SemNamed(symbolId, _name, _arguments) ->
            let grouped = heapNamedTypeConstructors(named)(environment)
            in
                if !heapMonomorphicUserAdt(named) || !(length(grouped) >= 2) || heapAdtExcludedFromReuse(named)(environment) || containsIntId(symbolId)(path)
                then false
                else
                    heapAllGroupedFields(given (fieldType) ->
                        heapDroppableLeafField(fieldType)(environment)(given (child) -> heapRuntimeRecordAdtLayout(child)(environment)([]) || heapRuntimeTcoOwnedChildAdtLayout(child)(environment)(symbolId :: path)))(grouped) && heapAnyGroupedField(given (fieldType) -> heapOwnsHeapField(fieldType)(environment))(grouped)
        | _ -> false
and heapRuntimeTcoOwnedChildAdtLayout (named: SemanticType) (environment: TypeEnvironment) (path: List(Int)) =
    match heapNamedTypeConstructors(named)(environment) with
        | (_constructorName, fieldTypes) :: [] ->
            if heapNamedTypeIsRecord(named)(environment)
            then heapRuntimeOwnedChildAdtLayout(named)(environment)(path)
            else
                if !heapMonomorphicUserAdt(named) || heapAdtExcludedFromReuse(named)(environment)
                then false
                else
                    heapAllTypes(given (fieldType) -> heapTcoOwnedChildField(fieldType)(environment))(fieldTypes) && heapAnyType(given (fieldType) -> heapOwnsHeapField(fieldType)(environment))(fieldTypes)
        | _ -> heapRuntimeOwnedChildAdtLayout(named)(environment)(path)

// A recursive-copy field: a scalar word or a direct reference to the enclosing type.
let heapRecursiveCopyField (symbolId: Int) (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let resolved = resolveLayoutType(semanticType)(environment)
    in
        if canArenaResetLayout(resolved)
        then true
        else
            match resolved with
                | SemNamed(childId, _name, _arguments) -> childId == symbolId
                | _ -> false)

// The first recursive runtime-RC boundary: a monomorphic user ADT whose fields are scalar words or
// direct references to the same type, with at least one such self-reference.
let heapRuntimeRecursiveCopyAdtLayout (named: SemanticType) (environment: TypeEnvironment) =
    match named with
        | SemNamed(symbolId, _name, _arguments) ->
            match heapNamedTypeConstructors(named)(environment) with
                | [] -> false
                | grouped ->
                    if !heapMonomorphicUserAdt(named) || heapAdtExcludedFromReuse(named)(environment)
                    then false
                    else
                        heapAllGroupedFields(given (fieldType) -> heapRecursiveCopyField(symbolId)(fieldType)(environment))(grouped) && heapAnyGroupedField(given (fieldType) -> heapOwnsHeapField(fieldType)(environment))(grouped)
        | _ -> false

// Whether a list element type can live in a runtime-managed TCO accumulator list: scalars and
// string-like leaves, lists and tuples of such elements, and shallow-copy, record, or owned-child
// ADTs.
let recursive heapRuntimeTcoListElementLayout (semanticType: SemanticType) (environment: TypeEnvironment) (path: List(Int)) =
    (let resolved = resolveLayoutType(semanticType)(environment)
    in
        if canArenaResetLayout(resolved)
        then true
        else
            match resolved with
                | SemString -> true
                | SemBytes -> true
                | SemBigInt -> true
                | SemList(element) -> heapRuntimeTcoListElementLayout(element)(environment)(path)
                | SemTuple(elements) ->
                    heapAllTypes(given (element) -> heapRuntimeTcoListElementLayout(element)(environment)(path))(elements)
                | SemNamed(_symbolId, _name, _arguments) -> heapAdtStructuralCopyKind(resolved)(environment) == ShallowCopy || heapRuntimeRecordAdtLayout(resolved)(environment)([]) || heapRuntimeOwnedChildAdtLayout(resolved)(environment)(path)
                | _ -> false)

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
                dropKind = heapDropKind(fieldType)(environment),
                copyKind = heapStructuralCopyKind(fieldType)(environment)
            ) :: heapChildrenOf(rest)(index + 1)(environment)

let recursive heapNamedTypeChildren (grouped: List((Str, List(SemanticType)))) (environment: TypeEnvironment) =
    match grouped with
        | [] -> []
        | (constructorName, fieldTypes) :: rest ->
            environment
            |> heapNamedTypeChildren(rest)
            |> append(heapChildrenOf(map(given (fieldType) -> (Some(constructorName), fieldType))(fieldTypes))(0)(environment))

let heapLayoutChildren (resolved: SemanticType) (environment: TypeEnvironment) =
    match resolved with
        | SemList(element) -> heapChildrenOf([(None, element), (None, resolved)])(0)(environment)
        | SemTuple(elements) ->
            heapChildrenOf(map(given (element) -> (None, element))(elements))(0)(environment)
        | SemNamed(_symbolId, _name, _arguments) ->
            heapNamedTypeChildren(heapNamedTypeConstructors(resolved)(environment))(environment)
        | _ -> []

let recursive heapAnyOwnedChild (children: List(HeapLayoutChild)) =
    match children with
        | [] -> false
        | HeapLayoutChild { dropKind = dropKind } :: rest ->
            if dropKind == NoChildDrop
            then heapAnyOwnedChild(rest)
            else true

// Only a named type has an outer cell the runtime can reuse; every other shape declines.
let heapRuntimeReuseSupport (resolved: SemanticType) (environment: TypeEnvironment) =
    match resolved with
        | SemNamed(_symbolId, _name, _arguments) ->
            HeapRuntimeReuseSupport(
                copyAdt = heapRuntimeCopyAdtLayout(resolved)(environment),
                recordAdt = heapRuntimeRecordAdtLayout(resolved)(environment)([]),
                ownedChildAdt = heapRuntimeOwnedChildAdtLayout(resolved)(environment)([]),
                tcoOwnedChildAdt = heapRuntimeTcoOwnedChildAdtLayout(resolved)(environment)([]),
                recursiveAdt = heapRuntimeRecursiveCopyAdtLayout(resolved)(environment)
            )
        | _ -> HeapRuntimeReuseSupport(copyAdt = false, recordAdt = false, ownedChildAdt = false, tcoOwnedChildAdt = false, recursiveAdt = false)

let heapOuterCellReuseSupported (support: HeapRuntimeReuseSupport) =
    match support with
        | HeapRuntimeReuseSupport { copyAdt = copyAdt, recordAdt = recordAdt, ownedChildAdt = ownedChildAdt, recursiveAdt = recursiveAdt } -> copyAdt || recordAdt || ownedChildAdt || recursiveAdt

let heapRejections (containsResource: Bool) (ownedChildrenDroppable: Bool) (containsUnresolvedType: Bool) (runtimeOuterCellReuseSupported: Bool) =
    HeapLayoutRejections(
        resourceOrBorrowedViewContainment = containsResource,
        unsupportedChildDropLayout = !ownedChildrenDroppable,
        unresolvedType = containsUnresolvedType,
        unsupportedOuterCellReuse = !runtimeOuterCellReuseSupported
    )

let heapAssembleFacts (resolved: SemanticType) (environment: TypeEnvironment) (children: List(HeapLayoutChild)) (containsResource: Bool) (ownedChildrenDroppable: Bool) (containsUnresolvedType: Bool) (support: HeapRuntimeReuseSupport) =
    match support with
        | HeapRuntimeReuseSupport { copyAdt = copyAdt, recordAdt = recordAdt, ownedChildAdt = ownedChildAdt, tcoOwnedChildAdt = tcoOwnedChildAdt } ->
            HeapLayoutFacts(
                containsResource = containsResource,
                containsUnresolvedType = containsUnresolvedType,
                containsOwnedChild = heapAnyOwnedChild(children),
                structuralCopy = heapStructuralCopyKind(resolved)(environment),
                arenaDeepCopySupported = heapArenaDeepCopyLayout(resolved)(environment)([]),
                ownedChildrenDroppable = ownedChildrenDroppable,
                runtimeOuterCellReuseSupported = heapOuterCellReuseSupported(support),
                runtimeCopyAdtSupported = copyAdt,
                runtimeRecordAdtSupported = recordAdt,
                runtimeOwnedChildAdtSupported = ownedChildAdt,
                runtimeTcoOwnedChildAdtSupported = tcoOwnedChildAdt,
                runtimeTcoListElementSupported = heapRuntimeTcoListElementLayout(resolved)(environment)([]),
                children = children,
                rejections = support
                |> heapOuterCellReuseSupported
                |> heapRejections(containsResource)(ownedChildrenDroppable)(containsUnresolvedType)
            )

let heapLayoutFactsOf (environment: TypeEnvironment) (resolved: SemanticType) =
    environment
    |> heapRuntimeReuseSupport(resolved)
    |> heapAssembleFacts(resolved)(environment)(heapLayoutChildren(resolved)(environment))(heapResourceBearing(resolved)(environment))(heapCanDropValueGraph(resolved)(environment))(heapContainsUnresolvedType(resolved)(environment)([]))

let classifyHeapLayout (semanticType: SemanticType) (environment: TypeEnvironment) =
    environment
    |> resolveLayoutType(semanticType)
    |> heapLayoutFactsOf(environment)
