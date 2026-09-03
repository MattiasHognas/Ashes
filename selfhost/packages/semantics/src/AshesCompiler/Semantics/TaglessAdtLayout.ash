// Decides which declared types use the tagless single-constructor cell layout and computes the
// byte offsets the lowering and the backend share for both ADT layouts. A type with exactly one
// constructor never needs a tag word: every value is that constructor, so its cell is laid out as
// [field0, field1, ...] with the payload at offset 0, while an ordinary cell is [tag, field0, ...].
// Every IR instruction that allocates, reads, or writes an ADT cell carries the decision as its
// trailing tagless flag, so the backend never consults a type to compute an offset.
//
// Invariants:
// - A tagless type has exactly one constructor, of arity at least one, that is neither
//   compiler-provided, zero-cost, a resource handle, nor resource-bearing in any field. A nullary
//   single-constructor type keeps its tag word so that a zero-byte cell can never let distinct
//   values share an address; a resource-bearing aggregate keeps it so that its deterministic-
//   cleanup walkers stay on the tagged layout.
// - The decision is made once per type declaration from its constructors' schemes, never from a
//   use site's instantiation: a generic single-constructor type is tagless whatever its arguments.
// - The resource walk instantiates each reached constructor's fields at the reaching type's own
//   arguments and stops at a type already on the path, so a recursive ADT reports a resource only
//   when one is reachable through a fresh, non-cyclic path.

import AshesCompiler.Semantics.Types
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
export (
    value adtWordSizeBytes,
    value adtTagOffsetBytes,
    value adtPayloadOffsetBytes,
    value adtFieldOffsetBytes,
    value adtAllocationSizeBytes,
    value isBuiltinResourceTypeName,
    value isCompilerProvidedTypeName,
    value constructorSchemeFields,
    value countConstructorsOfType,
    value isResourceBearingType,
    value isTaglessAdtConstructor,
)

let adtWordSizeBytes = 8

// A tagged cell's tag word is its first word.
let adtTagOffsetBytes = 0

let adtPayloadOffsetBytes (tagless: Bool) =
    if tagless
    then 0
    else adtWordSizeBytes

let adtFieldOffsetBytes (tagless: Bool) (fieldIndex: Int) = adtPayloadOffsetBytes(tagless) + fieldIndex * adtWordSizeBytes

let adtAllocationSizeBytes (tagless: Bool) (fieldCount: Int) = adtPayloadOffsetBytes(tagless) + fieldCount * adtWordSizeBytes

// Built-in handle types whose values own an operating-system resource.
let isBuiltinResourceTypeName (name: Str) = name == "Socket" || name == "TlsSocket" || name == "Process" || name == "FileHandle" || name == "JoinHandle"

// Types the compiler provides itself rather than user source declares; the backend's own
// intrinsics construct their cells with the tagged layout.
let isCompilerProvidedTypeName (name: Str) = name == "Unit" || name == "List" || name == "Maybe" || name == "Result" || name == "Task" || isBuiltinResourceTypeName(name)

let recursive constructorFieldShape (body: SemanticType) (reversedFields: List(SemanticType)) =
    match body with
        | SemFunction(parameter, result, _row) -> constructorFieldShape(result)(parameter :: reversedFields)
        | result -> (reverse(reversedFields), result)

// Peels a constructor's curried scheme body (field0 -> field1 -> ... -> T(args)) into its field
// types and its result type.
let constructorSchemeFields (scheme: TypeScheme) =
    match scheme with
        | TypeScheme { body = body } -> constructorFieldShape(body)([])

let constructorSchemeResultName (scheme: TypeScheme) =
    match constructorSchemeFields(scheme) with
        | (_fields, SemNamed(_symbolId, name, _arguments)) -> Some(name)
        | _ -> None

let recursive countConstructorsOfType (typeName: Str) (schemes: List(TypeScheme)) =
    match schemes with
        | [] -> 0
        | scheme :: rest ->
            match constructorSchemeResultName(scheme) with
                | Some(candidate) ->
                    if candidate == typeName
                    then 1 + countConstructorsOfType(typeName)(rest)
                    else countConstructorsOfType(typeName)(rest)
                | None -> countConstructorsOfType(typeName)(rest)

// Maps each type variable in a constructor's generalized result type to the corresponding
// concrete argument at the reaching use site.
let recursive resultParameterMapping (resultArguments: List(SemanticType)) (concreteArguments: List(SemanticType)) =
    match (resultArguments, concreteArguments) with
        | (SemVariable(variableId) :: restResult, concreteType :: restConcrete) -> (variableId, concreteType) :: resultParameterMapping(restResult)(restConcrete)
        | (_unmapped :: restResult, _concreteType :: restConcrete) -> resultParameterMapping(restResult)(restConcrete)
        | _ -> []

// The field types of every constructor of `typeName`, each instantiated at `arguments`.
let recursive instantiatedConstructorFields (typeName: Str) (arguments: List(SemanticType)) (schemes: List(TypeScheme)) =
    match schemes with
        | [] -> []
        | scheme :: rest ->
            match constructorSchemeFields(scheme) with
                | (fields, SemNamed(_symbolId, candidate, resultArguments)) ->
                    if candidate == typeName
                    then map(applySubstitution(resultParameterMapping(resultArguments)(arguments)))(fields) :: instantiatedConstructorFields(typeName)(arguments)(rest)
                    else instantiatedConstructorFields(typeName)(arguments)(rest)
                | _ -> instantiatedConstructorFields(typeName)(arguments)(rest)

let recursive containsTypeName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | head :: rest ->
            if head == name
            then true
            else containsTypeName(name)(rest)

// Whether a value of `semanticType` transitively owns a resource: a compiler-provided handle, a
// declared external resource (`isDeclaredResource` answers for both the opaque and the named
// spelling of its name), or an aggregate reaching one through a list, tuple, or constructor field.
let recursive isResourceBearingType (isDeclaredResource: Str -> Bool) (schemes: List(TypeScheme)) (visiting: List(Str)) (semanticType: SemanticType) =
    match semanticType with
        | SemOpaque(name) -> isDeclaredResource(name)
        | SemNamed(_symbolId, name, arguments) ->
            if isBuiltinResourceTypeName(name) || isDeclaredResource(name)
            then true
            else
                if containsTypeName(name)(visiting)
                then false
                else anyConstructorResourceBearing(isDeclaredResource)(schemes)(name :: visiting)(instantiatedConstructorFields(name)(arguments)(schemes))
        | SemTuple(elements) -> anyResourceBearingType(isDeclaredResource)(schemes)(visiting)(elements)
        | SemList(element) -> isResourceBearingType(isDeclaredResource)(schemes)(visiting)(element)
        | _ -> false
and anyResourceBearingType (isDeclaredResource: Str -> Bool) (schemes: List(TypeScheme)) (visiting: List(Str)) (types: List(SemanticType)) =
    match types with
        | [] -> false
        | head :: rest ->
            if isResourceBearingType(isDeclaredResource)(schemes)(visiting)(head)
            then true
            else anyResourceBearingType(isDeclaredResource)(schemes)(visiting)(rest)
and anyConstructorResourceBearing (isDeclaredResource: Str -> Bool) (schemes: List(TypeScheme)) (visiting: List(Str)) (grouped: List(List(SemanticType))) =
    match grouped with
        | [] -> false
        | fields :: rest ->
            if anyResourceBearingType(isDeclaredResource)(schemes)(visiting)(fields)
            then true
            else anyConstructorResourceBearing(isDeclaredResource)(schemes)(visiting)(rest)

// Whether the constructor `scheme` describes is the sole constructor of a tagless type. `schemes`
// must hold every registered constructor scheme, the type's own included, so that the sibling
// count and the resource walk see the whole declaration.
let isTaglessAdtConstructor (isDeclaredResource: Str -> Bool) (schemes: List(TypeScheme)) (isZeroCost: Bool) (scheme: TypeScheme) =
    match constructorSchemeFields(scheme) with
        | ([], _result) -> false
        | (fields, SemNamed(_symbolId, typeName, _arguments)) ->
            if isZeroCost || isCompilerProvidedTypeName(typeName) || isDeclaredResource(typeName) || countConstructorsOfType(typeName)(schemes) != 1
            then false
            else !anyResourceBearingType(isDeclaredResource)(schemes)([])(fields)
        | _ -> false
