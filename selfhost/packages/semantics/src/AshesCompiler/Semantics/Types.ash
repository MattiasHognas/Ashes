// Defines semantic types, trait constraints, substitutions, and fresh-variable supplies.
//
// Invariants:
// - Named type identity is carried by stable symbols rather than display text.
// - Substitutions are applied recursively through every type and constraint component.
// - Trait constraints have a deterministic canonical order with exact duplicates removed.

import Ashes.Collection.List.sortBy
import Ashes.Text.compare as compareText
export (
    type SemanticType(..),
    type TraitConstraint(..),
    type TypeScheme(..),
    type TypeVariableSupply(..),
    value initialTypeVariableSupply,
    value freshTypeVariable,
    value occursInType,
    value applySubstitution,
    value formatSemanticType,
    value traitConstraintStableKey,
    value canonicalizeTraitConstraints,
)

type SemanticType =
    | SemInt
    | SemUInt(Int)
    | SemFloat
    | SemBigInt
    | SemString
    | SemRune
    | SemBytes
    | SemBool
    | SemNever
    | SemList(SemanticType)
    | SemTuple(List(SemanticType))
    | SemFunction(SemanticType, SemanticType, Maybe(SemanticType))
    | SemVariable(Int)
    | SemCapability(Str, List(SemanticType))
    | SemRow(List(SemanticType), Maybe(SemanticType))
    | SemNamed(Int, Str, List(SemanticType))
    | SemParameter(Int, Str)
    | SemOpaque(Str)
    | SemPointer(SemanticType)
    deriving {Eq, Show}

type TraitConstraint =
    | traitName: Str
    | typeArguments: List(SemanticType)
    deriving {Eq, Show}

type TypeScheme =
    | quantified: List((Int, Str))
    | body: SemanticType
    | constraints: List(TraitConstraint)
    deriving {Eq, Show}

type TypeVariableSupply =
    | nextId: Int
    deriving {Eq, Show}

let initialTypeVariableSupply : Unit -> TypeVariableSupply =
    given (_unit) -> TypeVariableSupply(nextId = 0)

let freshTypeVariable : TypeVariableSupply -> (SemanticType, TypeVariableSupply) =
    given (supply) ->
        match supply with
            | TypeVariableSupply { nextId = nextId } -> (SemVariable(nextId), TypeVariableSupply(nextId = nextId + 1))

let recursive anyType : (SemanticType -> Bool) -> List(SemanticType) -> Bool =
    given (predicate) ->
        given (values) ->
            match values with
                | [] -> false
                | head :: tail ->
                    if predicate(head)
                    then true
                    else anyType(predicate)(tail)

let recursive occursInType : Int -> SemanticType -> Bool =
    given (variableId) ->
        given (semanticType) ->
            match semanticType with
                | SemVariable(candidateId) -> variableId == candidateId
                | SemList(element) -> occursInType(variableId)(element)
                | SemTuple(elements) ->
                    anyType(occursInType(variableId))(elements)
                | SemFunction(argument, result, capabilityRow) ->
                    if occursInType(variableId)(argument)
                    then true
                    else
                        if occursInType(variableId)(result)
                        then true
                        else
                            match capabilityRow with
                                | None -> false
                                | Some(row) -> occursInType(variableId)(row)
                | SemCapability(_name, arguments) ->
                    anyType(occursInType(variableId))(arguments)
                | SemRow(capabilities, tail) ->
                    if anyType(occursInType(variableId))(capabilities)
                    then true
                    else
                        match tail with
                            | None -> false
                            | Some(tailType) -> occursInType(variableId)(tailType)
                | SemNamed(_symbolId, _name, arguments) ->
                    anyType(occursInType(variableId))(arguments)
                | SemPointer(pointee) -> occursInType(variableId)(pointee)
                | _ -> false

let recursive lookupSubstitution : Int -> List((Int, SemanticType)) -> Maybe(SemanticType) =
    given (variableId) ->
        given (substitution) ->
            match substitution with
                | [] -> None
                | (candidateId, replacement) :: tail ->
                    if variableId == candidateId
                    then Some(replacement)
                    else lookupSubstitution(variableId)(tail)

let recursive substituteTypes : List((Int, SemanticType)) -> List(SemanticType) -> List(SemanticType) =
    given (substitution) ->
        given (values) ->
            match values with
                | [] -> []
                | head :: tail ->
                    let substitutedHead = applySubstitution(substitution)(head)
                    in
                        let substitutedTail = substituteTypes(substitution)(tail)
                        in substitutedHead :: substitutedTail
and applySubstitution : List((Int, SemanticType)) -> SemanticType -> SemanticType =
    given (substitution) ->
        given (semanticType) ->
            match semanticType with
                | SemVariable(variableId) ->
                    match lookupSubstitution(variableId)(substitution) with
                        | None -> semanticType
                        | Some(replacement) -> applySubstitution(substitution)(replacement)
                | SemList(element) ->
                    let substitutedElement = applySubstitution(substitution)(element)
                    in SemList(substitutedElement)
                | SemTuple(elements) ->
                    let substitutedElements = substituteTypes(substitution)(elements)
                    in SemTuple(substitutedElements)
                | SemFunction(argument, result, capabilityRow) ->
                    let substitutedArgument = applySubstitution(substitution)(argument)
                    in
                        let substitutedResult = applySubstitution(substitution)(result)
                        in
                            let substitutedRow =
                                match capabilityRow with
                                    | None -> None
                                    | Some(row) ->
                                        row
                                        |> applySubstitution(substitution)
                                        |> Some
                            in SemFunction(substitutedArgument)(substitutedResult)(substitutedRow)
                | SemCapability(name, arguments) ->
                    let substitutedArguments = substituteTypes(substitution)(arguments)
                    in SemCapability(name)(substitutedArguments)
                | SemRow(capabilities, tail) ->
                    let substitutedCapabilities = substituteTypes(substitution)(capabilities)
                    in
                        let substitutedTail =
                            match tail with
                                | None -> None
                                | Some(tailType) ->
                                    tailType
                                    |> applySubstitution(substitution)
                                    |> Some
                        in SemRow(substitutedCapabilities)(substitutedTail)
                | SemNamed(symbolId, name, arguments) ->
                    let substitutedArguments = substituteTypes(substitution)(arguments)
                    in SemNamed(symbolId)(name)(substitutedArguments)
                | SemPointer(pointee) ->
                    let substitutedPointee = applySubstitution(substitution)(pointee)
                    in SemPointer(substitutedPointee)
                | _ -> semanticType

let recursive joinTypes : Str -> (SemanticType -> Str) -> List(SemanticType) -> Str =
    given (separator) ->
        given (render) ->
            given (values) ->
                match values with
                    | [] -> ""
                    | head :: tail ->
                        let recursive renderTail remaining =
                            match remaining with
                                | [] -> ""
                                | item :: rest ->
                                    let itemText = render(item)
                                    in
                                        let tailText = renderTail(rest)
                                        in separator + itemText + tailText
                        in
                            let headText = render(head)
                            in
                                let tailText = renderTail(tail)
                                in headText + tailText

let recursive stableKeyZeros count =
    if count <= 0
    then ""
    else "0" + stableKeyZeros(count - 1)

let stableVariableKey variableId =
    (let text = Ashes.Text.fromInt(variableId)
    in
        let width =
            text
            |> Ashes.Byte.fromText
            |> Ashes.Byte.length
        in stableKeyZeros(10 - width) + text)

let recursive semanticTypeStableKey semanticType =
    match semanticType with
        | SemInt -> "Int"
        | SemUInt(bits) -> "u" + Ashes.Text.fromInt(bits)
        | SemFloat -> "Float"
        | SemBigInt -> "BigInt"
        | SemString -> "Str"
        | SemRune -> "Rune"
        | SemBytes -> "Bytes"
        | SemBool -> "Bool"
        | SemNever -> "Never"
        | SemVariable(variableId) -> "?" + stableVariableKey(variableId)
        | SemParameter(_symbolId, name) -> "'" + name
        | SemList(element) -> "List(" + semanticTypeStableKey(element) + ")"
        | SemTuple(elements) -> "Tuple(" + joinTypes(",")(semanticTypeStableKey)(elements) + ")"
        | SemFunction(argument, result, _capabilityRow) ->
            "Fun(" + semanticTypeStableKey(
                argument
            ) + "," + semanticTypeStableKey(result) + ")"
        | SemNamed(_symbolId, name, arguments) -> name + "(" + joinTypes(",")(semanticTypeStableKey)(arguments) + ")"
        | SemPointer(pointee) -> "Ptr(" + semanticTypeStableKey(pointee) + ")"
        | SemOpaque(name) -> "Opaque(" + name + ")"
        | SemCapability(name, arguments) ->
            "Capability(" + name + "," + joinTypes(
                ",",
                semanticTypeStableKey,
                arguments
            ) + ")"
        | SemRow(capabilities, tail) ->
            let tailKey =
                match tail with
                    | None -> ""
                    | Some(tailType) -> semanticTypeStableKey(tailType)
            in "Row(" + joinTypes(",")(semanticTypeStableKey)(capabilities) + "|" + tailKey + ")"

let traitConstraintStableKey constraint =
    match constraint with
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } ->
            traitName + "(" + joinTypes(
                ",",
                semanticTypeStableKey,
                typeArguments
            ) + ")"

let recursive removeDuplicateTraitConstraints constraints =
    match constraints with
        | [] -> []
        | head :: tail ->
            let key = traitConstraintStableKey(head)
            in
                let recursive skipDuplicates remaining =
                    match remaining with
                        | [] -> []
                        | candidate :: rest ->
                            if traitConstraintStableKey(candidate) == key
                            then skipDuplicates(rest)
                            else candidate :: rest
                in
                    head :: removeDuplicateTraitConstraints(skipDuplicates(tail))

let canonicalizeTraitConstraints constraints =
    constraints
    |> sortBy(given (left) ->
        given (right) ->
            compareText(traitConstraintStableKey(left))(traitConstraintStableKey(right)) <= 0)
    |> removeDuplicateTraitConstraints

let recursive formatSemanticType : SemanticType -> Str =
    given (semanticType) ->
        match semanticType with
            | SemInt -> "Int"
            | SemUInt(bits) -> "UInt" + Ashes.Text.fromInt(bits)
            | SemFloat -> "Float"
            | SemBigInt -> "BigInt"
            | SemString -> "Str"
            | SemRune -> "Rune"
            | SemBytes -> "Bytes"
            | SemBool -> "Bool"
            | SemNever -> "Never"
            | SemList(element) ->
                let elementText = formatSemanticType(element)
                in "List(" + elementText + ")"
            | SemTuple(elements) ->
                let elementsText = joinTypes(", ")(formatSemanticType)(elements)
                in "(" + elementsText + ")"
            | SemFunction(argument, result, capabilityRow) ->
                let argumentText =
                    match argument with
                        | SemFunction(_, _, _) ->
                            let renderedArgument = formatSemanticType(argument)
                            in "(" + renderedArgument + ")"
                        | _ -> formatSemanticType(argument)
                in
                    let resultText = formatSemanticType(result)
                    in
                        let rowText =
                            match capabilityRow with
                                | None -> ""
                                | Some(row) ->
                                    let renderedRow = formatSemanticType(row)
                                    in " needs " + renderedRow
                        in argumentText + " -> " + resultText + rowText
            | SemVariable(variableId) -> "?" + Ashes.Text.fromInt(variableId)
            | SemCapability(name, []) -> name
            | SemCapability(name, arguments) ->
                let argumentsText = joinTypes(", ")(formatSemanticType)(arguments)
                in name + "(" + argumentsText + ")"
            | SemRow(capabilities, tail) ->
                let capabilitiesText = joinTypes(", ")(formatSemanticType)(capabilities)
                in
                    match (capabilities, tail) with
                        | ([], None) -> "{}"
                        | ([], Some(tailType)) -> formatSemanticType(tailType)
                        | (_, None) -> "{" + capabilitiesText + "}"
                        | (_, Some(tailType)) ->
                            let tailText = formatSemanticType(tailType)
                            in "{" + capabilitiesText + " | " + tailText + "}"
            | SemNamed(_symbolId, name, []) -> name
            | SemNamed(_symbolId, name, arguments) ->
                let argumentsText = joinTypes(", ")(formatSemanticType)(arguments)
                in name + "(" + argumentsText + ")"
            | SemParameter(_symbolId, name) -> name
            | SemOpaque(name) -> name
            | SemPointer(pointee) ->
                let pointeeText = formatSemanticType(pointee)
                in "*" + pointeeText
