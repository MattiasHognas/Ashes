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
)

type SemanticType =
    | TypeInt
    | TypeUInt(Int)
    | TypeFloat
    | TypeBigInt
    | TypeString
    | TypeRune
    | TypeBytes
    | TypeBool
    | TypeNever
    | TypeList(SemanticType)
    | TypeTuple(List(SemanticType))
    | TypeFunction(SemanticType, SemanticType, Maybe(SemanticType))
    | TypeVariable(Int)
    | TypeCapability(Str, List(SemanticType))
    | TypeRow(List(SemanticType), Maybe(SemanticType))
    | TypeNamed(Int, Str, List(SemanticType))
    | TypeParameter(Int, Str)
    | TypeOpaque(Str)
    | TypePointer(SemanticType)
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
            | TypeVariableSupply { nextId = nextId } -> (TypeVariable(nextId), TypeVariableSupply(nextId = nextId + 1))

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
                | TypeVariable(candidateId) -> variableId == candidateId
                | TypeList(element) -> occursInType(variableId)(element)
                | TypeTuple(elements) -> anyType(occursInType(variableId))(elements)
                | TypeFunction(argument, result, capabilityRow) ->
                    if occursInType(variableId)(argument)
                    then true
                    else
                        if occursInType(variableId)(result)
                        then true
                        else
                            match capabilityRow with
                                | None -> false
                                | Some(row) -> occursInType(variableId)(row)
                | TypeCapability(_name, arguments) -> anyType(occursInType(variableId))(arguments)
                | TypeRow(capabilities, tail) ->
                    if anyType(occursInType(variableId))(capabilities)
                    then true
                    else
                        match tail with
                            | None -> false
                            | Some(tailType) -> occursInType(variableId)(tailType)
                | TypeNamed(_symbolId, _name, arguments) -> anyType(occursInType(variableId))(arguments)
                | TypePointer(pointee) -> occursInType(variableId)(pointee)
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
                | TypeVariable(variableId) ->
                    match lookupSubstitution(variableId)(substitution) with
                        | None -> semanticType
                        | Some(replacement) -> replacement
                | TypeList(element) ->
                    let substitutedElement = applySubstitution(substitution)(element)
                    in TypeList(substitutedElement)
                | TypeTuple(elements) ->
                    let substitutedElements = substituteTypes(substitution)(elements)
                    in TypeTuple(substitutedElements)
                | TypeFunction(argument, result, capabilityRow) ->
                    let substitutedArgument = applySubstitution(substitution)(argument)
                    in
                        let substitutedResult = applySubstitution(substitution)(result)
                        in
                            let substitutedRow =
                                match capabilityRow with
                                    | None -> None
                                    | Some(row) -> Some(applySubstitution(substitution)(row))
                            in TypeFunction(substitutedArgument)(substitutedResult)(substitutedRow)
                | TypeCapability(name, arguments) ->
                    let substitutedArguments = substituteTypes(substitution)(arguments)
                    in TypeCapability(name)(substitutedArguments)
                | TypeRow(capabilities, tail) ->
                    let substitutedCapabilities = substituteTypes(substitution)(capabilities)
                    in
                        let substitutedTail =
                            match tail with
                                | None -> None
                                | Some(tailType) -> Some(applySubstitution(substitution)(tailType))
                        in TypeRow(substitutedCapabilities)(substitutedTail)
                | TypeNamed(symbolId, name, arguments) ->
                    let substitutedArguments = substituteTypes(substitution)(arguments)
                    in TypeNamed(symbolId)(name)(substitutedArguments)
                | TypePointer(pointee) ->
                    let substitutedPointee = applySubstitution(substitution)(pointee)
                    in TypePointer(substitutedPointee)
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

let recursive formatSemanticType : SemanticType -> Str =
    given (semanticType) ->
        match semanticType with
            | TypeInt -> "Int"
            | TypeUInt(bits) -> "UInt" + Ashes.Text.fromInt(bits)
            | TypeFloat -> "Float"
            | TypeBigInt -> "BigInt"
            | TypeString -> "Str"
            | TypeRune -> "Rune"
            | TypeBytes -> "Bytes"
            | TypeBool -> "Bool"
            | TypeNever -> "Never"
            | TypeList(element) ->
                let elementText = formatSemanticType(element)
                in "List(" + elementText + ")"
            | TypeTuple(elements) ->
                let elementsText = joinTypes(", ")(formatSemanticType)(elements)
                in "(" + elementsText + ")"
            | TypeFunction(argument, result, capabilityRow) ->
                let argumentText =
                    match argument with
                        | TypeFunction(_, _, _) ->
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
            | TypeVariable(variableId) -> "?" + Ashes.Text.fromInt(variableId)
            | TypeCapability(name, []) -> name
            | TypeCapability(name, arguments) ->
                let argumentsText = joinTypes(", ")(formatSemanticType)(arguments)
                in name + "(" + argumentsText + ")"
            | TypeRow(capabilities, tail) ->
                let capabilitiesText = joinTypes(", ")(formatSemanticType)(capabilities)
                in
                    match (capabilities, tail) with
                        | ([], None) -> "{}"
                        | ([], Some(tailType)) -> formatSemanticType(tailType)
                        | (_, None) -> "{" + capabilitiesText + "}"
                        | (_, Some(tailType)) ->
                            let tailText = formatSemanticType(tailType)
                            in "{" + capabilitiesText + " | " + tailText + "}"
            | TypeNamed(_symbolId, name, []) -> name
            | TypeNamed(_symbolId, name, arguments) ->
                let argumentsText = joinTypes(", ")(formatSemanticType)(arguments)
                in name + "(" + argumentsText + ")"
            | TypeParameter(_symbolId, name) -> name
            | TypeOpaque(name) -> name
            | TypePointer(pointee) ->
                let pointeeText = formatSemanticType(pointee)
                in "*" + pointeeText
