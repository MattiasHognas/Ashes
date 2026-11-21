// Defines semantic types, trait constraints, substitutions, and fresh-variable supplies.
//
// Invariants:
// - Named type identity is carried by stable symbols rather than display text.
// - Substitutions are applied recursively through every type and constraint component.
// - Trait constraints have a deterministic canonical order with exact duplicates removed.

import Ashes.Collection.List.sortBy
import Ashes.Collection.Map.MapTree
import Ashes.Text.compare as compareText
export (
    type SemanticType(..),
    type TraitConstraint(..),
    type TypeScheme(..),
    type NodeTypeNote(..),
    type TypeVariableSupply(..),
    type EvidenceParameterUse(..),
    type EvidenceCallFlow(..),
    value initialTypeVariableSupply,
    value typeVariableSupplyFrom,
    value reservedInferenceTypeVariableCount,
    value freshTypeVariable,
    value currentNodeOf,
    value nodeNotesOf,
    value withCurrentNode,
    value withNodeNote,
    value withoutNodeNotes,
    value occursInType,
    value applySubstitution,
    value applySubstitutionMap,
    value applySubstitutionUsing,
    value extendSubstitutionMap,
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

// What the inference learned at one identified syntax node, as written while it runs: the types
// still hold inference variables, which the enclosing declaration resolves when it finishes.
type NodeTypeNote =
    // The node and its type.
    | NodeTypeNote(Int, SemanticType)
    // An operator node, the trait its operator maps to, and the type of its operands.
    | NodeOperatorNote(Int, Str, SemanticType)
    // A reference to a generic binding: the type each quantified variable of its scheme took here,
    // the trait requirements of the scheme at those types, and those of them that mention a
    // quantified variable, in the scheme's order: the dictionaries the reference passes.
    | NodeInstantiationNote(Int, List((Int, SemanticType)), List(TraitConstraint), List(TraitConstraint), List((TraitConstraint, Int)))
    | NodeEvidenceProfileNote(Int, List(Int))
    // A name a node brings into scope and its scheme. The node is the `let` for a binding and the
    // arm body for a pattern variable.
    | NodeBindingNote(Int, Str, TypeScheme)
    // A top-level binding and its scheme, against the root node of the binding's value.
    | NodeDeclarationNote(Int, Str, TypeScheme)
    deriving {Eq, Show}

// The supply of fresh type variables, which every inference step takes and hands on. It also
// carries what is recorded against identified nodes, because nothing else is threaded through
// every step in order: `currentNode` is the identified node being inferred, or -1, and `notes`
// holds the youngest note first. An inference that is abandoned abandons its notes with its
// supply.
// A source callback is called with arguments at a stage of its enclosing function. A returned
// callback/result can consume additional caller arguments. Parameter identities in a walk
// are numbered lambda nodes; in a published source profile they are zero-based argument slots.
type EvidenceParameterUse =
    | parameter: Int
    | path: List(Str)
    | applied: Int
    | stage: Int
    | returned: Bool
    deriving {Eq, Show}

type EvidenceCallFlow =
    | SourceEvidenceFlow(List(EvidenceParameterUse), List(EvidenceParameterUse))
    | FormalEvidenceParameter(Int, List(Str))
    | AggregateEvidenceFlow(List((Str, EvidenceCallFlow)))
    | ConstructorEvidenceFlow(Int)
    | RecursiveEvidenceReference(Str)
    | AlternativeEvidenceFlow(List(EvidenceCallFlow))
    | MemberEvidenceFlow(List((TraitConstraint, Int)), EvidenceCallFlow)
    | UnknownEvidenceFlow
    deriving {Eq, Show}

type TypeVariableSupply =
    | nextId: Int
    | currentNode: Int
    | notes: List(NodeTypeNote)
    | evidenceSites: List((Int, List((TraitConstraint, Int))))
    | evidenceFlowSites: List((Int, EvidenceCallFlow))
    deriving {Eq, Show}

let initialTypeVariableSupply : Unit -> TypeVariableSupply =
    given (_unit) -> TypeVariableSupply(nextId = 0, currentNode = -1, notes = [], evidenceSites = [], evidenceFlowSites = [])

// A supply that starts above a block of ids some statically embedded `TypeScheme` already
// quantifies over. Such a scheme is written before any supply runs, so its quantified ids are fixed
// constants; if a live supply reissues one, `TypeSchemes.ash`'s `instantiate` mints the substitution
// `(id, SemVariable(id))` and `applySubstitution` recurses on that self-mapping forever. Starting
// past the reservation is the guarantee; leaving a gap and hoping no program grows into it is not.
let typeVariableSupplyFrom : Int -> TypeVariableSupply =
    given (nextId) -> TypeVariableSupply(nextId = nextId, currentNode = -1, notes = [], evidenceSites = [], evidenceFlowSites = [])

// The id an inference supply starts above. Two blocks are minted before any supply runs and must
// never be reissued: `CoreBuiltinLowering`'s builtin signatures quantify over
// `[0, reservedBuiltinTypeVariableCount)`, and `StandardTraits`' `addStandardTrait` numbers its
// trait parameters from 1000. Starting past both is the guarantee; relying on the gap between them
// staying empty is not. Raise this if either block grows past it.
let reservedInferenceTypeVariableCount = 2000

let freshTypeVariable : TypeVariableSupply -> (SemanticType, TypeVariableSupply) =
    given (supply) -> (SemVariable(supply.nextId), (supply with nextId = supply.nextId + 1))

let withCurrentNode (node: Int) (supply: TypeVariableSupply) = supply with currentNode = node

let nodeNotesOf (supply: TypeVariableSupply) = supply.notes

let currentNodeOf (supply: TypeVariableSupply) = supply.currentNode

let withNodeNote (note: NodeTypeNote) (supply: TypeVariableSupply) = supply with notes = note :: supply.notes

let withoutNodeNotes (supply: TypeVariableSupply) = supply with notes = [], evidenceSites = [], evidenceFlowSites = []

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

// The substitution walk over a bare lookup, so one implementation serves both shapes a
// substitution is kept in: the association list a unification builds and hands back, and the
// variable-keyed map a whole program's grows into.
let recursive substituteTypesIn : (Int -> Maybe(SemanticType)) -> List(SemanticType) -> List(SemanticType) =
    given (lookup) ->
        given (values) ->
            match values with
                | [] -> []
                | head :: tail ->
                    let substitutedHead = applySubstitutionIn(lookup)(head)
                    in
                        let substitutedTail = substituteTypesIn(lookup)(tail)
                        in substitutedHead :: substitutedTail
and applySubstitutionIn : (Int -> Maybe(SemanticType)) -> SemanticType -> SemanticType =
    given (lookup) ->
        given (semanticType) ->
            match semanticType with
                | SemVariable(variableId) ->
                    match lookup(variableId) with
                        | None -> semanticType
                        | Some(replacement) -> applySubstitutionIn(lookup)(replacement)
                | SemList(element) ->
                    let substitutedElement = applySubstitutionIn(lookup)(element)
                    in SemList(substitutedElement)
                | SemTuple(elements) ->
                    let substitutedElements = substituteTypesIn(lookup)(elements)
                    in SemTuple(substitutedElements)
                | SemFunction(argument, result, capabilityRow) ->
                    let substitutedArgument = applySubstitutionIn(lookup)(argument)
                    in
                        let substitutedResult = applySubstitutionIn(lookup)(result)
                        in
                            let substitutedRow =
                                match capabilityRow with
                                    | None -> None
                                    | Some(row) ->
                                        row
                                        |> applySubstitutionIn(lookup)
                                        |> Some
                            in SemFunction(substitutedArgument)(substitutedResult)(substitutedRow)
                | SemCapability(name, arguments) ->
                    let substitutedArguments = substituteTypesIn(lookup)(arguments)
                    in SemCapability(name)(substitutedArguments)
                | SemRow(capabilities, tail) ->
                    let substitutedCapabilities = substituteTypesIn(lookup)(capabilities)
                    in
                        let substitutedTail =
                            match tail with
                                | None -> None
                                | Some(tailType) ->
                                    tailType
                                    |> applySubstitutionIn(lookup)
                                    |> Some
                        in SemRow(substitutedCapabilities)(substitutedTail)
                | SemNamed(symbolId, name, arguments) ->
                    let substitutedArguments = substituteTypesIn(lookup)(arguments)
                    in SemNamed(symbolId)(name)(substitutedArguments)
                | SemPointer(pointee) ->
                    let substitutedPointee = applySubstitutionIn(lookup)(pointee)
                    in SemPointer(substitutedPointee)
                | _ -> semanticType

let recursive anyBoundIn : (Int -> Maybe(SemanticType)) -> List(SemanticType) -> Bool =
    given (lookup) ->
        given (values) ->
            match values with
                | [] -> false
                | head :: tail ->
                    if hasBoundVariable(lookup)(head)
                    then true
                    else anyBoundIn(lookup)(tail)
and hasBoundVariable : (Int -> Maybe(SemanticType)) -> SemanticType -> Bool =
    given (lookup) ->
        given (semanticType) ->
            match semanticType with
                | SemVariable(variableId) ->
                    match lookup(variableId) with
                        | None -> false
                        | Some(_replacement) -> true
                | SemList(element) -> hasBoundVariable(lookup)(element)
                | SemTuple(elements) -> anyBoundIn(lookup)(elements)
                | SemFunction(argument, result, capabilityRow) ->
                    if hasBoundVariable(lookup)(argument)
                    then true
                    else
                        if hasBoundVariable(lookup)(result)
                        then true
                        else
                            match capabilityRow with
                                | None -> false
                                | Some(row) -> hasBoundVariable(lookup)(row)
                | SemCapability(_name, arguments) -> anyBoundIn(lookup)(arguments)
                | SemRow(capabilities, tail) ->
                    if anyBoundIn(lookup)(capabilities)
                    then true
                    else
                        match tail with
                            | None -> false
                            | Some(tailType) -> hasBoundVariable(lookup)(tailType)
                | SemNamed(_symbolId, _name, arguments) -> anyBoundIn(lookup)(arguments)
                | SemPointer(pointee) -> hasBoundVariable(lookup)(pointee)
                | _ -> false

// The walk above rebuilds every node it passes, so a type the substitution binds nothing in is
// answered as it is: most types resolved during lowering already are, and rebuilding them was the
// largest share of what resolving allocated.
let resolveIn : (Int -> Maybe(SemanticType)) -> SemanticType -> SemanticType =
    given (lookup) ->
        given (semanticType) ->
            if hasBoundVariable(lookup)(semanticType)
            then applySubstitutionIn(lookup)(semanticType)
            else semanticType

// Scheme application supplies a lookup that hides bound quantified variables.
let applySubstitutionUsing lookup semanticType = resolveIn(lookup)(semanticType)

let substituteTypes : List((Int, SemanticType)) -> List(SemanticType) -> List(SemanticType) =
    given (substitution) ->
        substituteTypesIn(given (variableId) -> lookupSubstitution(variableId)(substitution))

let applySubstitution : List((Int, SemanticType)) -> SemanticType -> SemanticType =
    given (substitution) ->
        resolveIn(given (variableId) -> lookupSubstitution(variableId)(substitution))

// The same walk over a substitution kept as a variable-keyed map. A whole program's substitution
// reaches tens of thousands of entries and every type it resolves walks it once per variable, so
// the association list a unification's own handful of bindings lives in is the wrong shape for it.
// Writing the walk out again instead of reaching it through this lookup was measured and is worth
// 0.5% of a 45 GB compile, which does not buy sixty duplicated lines.
let applySubstitutionMap : MapTree(Int, SemanticType) -> SemanticType -> SemanticType =
    given (substitution) ->
        resolveIn(given (variableId) -> Ashes.Collection.Map.getInt(variableId)(substitution))

// A unification's bindings added to a program's substitution, keeping the association list's own
// rule that the first entry for a variable is the one that answers: the additions are written
// youngest last, so the youngest is what a lookup finds.
let recursive extendSubstitutionMap : List((Int, SemanticType)) -> MapTree(Int, SemanticType) -> MapTree(Int, SemanticType) =
    given (added) ->
        given (substitution) ->
            match added with
                | [] -> substitution
                | (variableId, replacement) :: rest ->
                    substitution
                    |> extendSubstitutionMap(rest)
                    |> Ashes.Collection.Map.setInt(variableId)(replacement)

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
