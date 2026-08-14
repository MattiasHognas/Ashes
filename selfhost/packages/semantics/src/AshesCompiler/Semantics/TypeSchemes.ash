import AshesCompiler.Semantics.Types
export (
    type InstantiationResult(..),
    value freeTypeVariables,
    value freeSchemeVariables,
    value generalize,
    value instantiate,
)

type InstantiationResult =
    | semanticType: SemanticType
    | constraints: List(TraitConstraint)
    | supply: TypeVariableSupply

let sameVariableId : Int -> Int -> Bool =
    given (left) ->
        given (right) -> left == right

let recursive containsVariable variableId variables =
    match variables with
        | [] -> false
        | head :: tail ->
            if sameVariableId(variableId)(head)
            then true
            else containsVariable(variableId)(tail)

let addVariable variableId variables =
    if containsVariable(variableId)(variables)
    then variables
    else variableId :: variables

let recursive mergeVariables left right =
    match left with
        | [] -> right
        | head :: tail -> mergeVariables(tail)(addVariable(head)(right))

let recursive freeTypes semanticTypes =
    match semanticTypes with
        | [] -> []
        | head :: tail ->
            let headVariables = freeTypeVariables(head)
            in
                let tailVariables = freeTypes(tail)
                in mergeVariables(headVariables)(tailVariables)
and freeTypeVariables semanticType =
    match semanticType with
        | SemVariable(variableId) -> [variableId]
        | SemList(element) -> freeTypeVariables(element)
        | SemTuple(elements) -> freeTypes(elements)
        | SemFunction(argument, result, capabilityRow) ->
            let argumentVariables = freeTypeVariables(argument)
            in
                let resultVariables = freeTypeVariables(result)
                in
                    let rowVariables =
                        match capabilityRow with
                            | None -> []
                            | Some(row) -> freeTypeVariables(row)
                    in mergeVariables(argumentVariables)(mergeVariables(resultVariables)(rowVariables))
        | SemCapability(_name, arguments) -> freeTypes(arguments)
        | SemRow(capabilities, tail) ->
            let capabilityVariables = freeTypes(capabilities)
            in
                let tailVariables =
                    match tail with
                        | None -> []
                        | Some(tailType) -> freeTypeVariables(tailType)
                in mergeVariables(capabilityVariables)(tailVariables)
        | SemNamed(_symbolId, _name, arguments) -> freeTypes(arguments)
        | SemPointer(pointee) -> freeTypeVariables(pointee)
        | _ -> []

let freeConstraintVariables constraint =
    match constraint with
        | TraitConstraint { traitName = _name, typeArguments = arguments } -> freeTypes(arguments)

let recursive freeConstraintsVariables constraints =
    match constraints with
        | [] -> []
        | head :: tail -> mergeVariables(freeConstraintVariables(head))(freeConstraintsVariables(tail))

let recursive removeQuantified quantified variables =
    match quantified with
        | [] -> variables
        | (variableId, _name) :: tail ->
            let recursive remove remaining =
                match remaining with
                    | [] -> []
                    | head :: rest ->
                        if sameVariableId(head)(variableId)
                        then remove(rest)
                        else head :: remove(rest)
            in removeQuantified(tail)(remove(variables))

let freeSchemeVariables scheme =
    match scheme with
        | TypeScheme { quantified = quantified, body = body, constraints = constraints } ->
            let allVariables = mergeVariables(freeTypeVariables(body))(freeConstraintsVariables(constraints))
            in removeQuantified(quantified)(allVariables)

let recursive freeEnvironmentVariables environment =
    match environment with
        | [] -> []
        | scheme :: tail -> mergeVariables(freeSchemeVariables(scheme))(freeEnvironmentVariables(tail))

let recursive removeEnvironmentVariables environmentVariables variables =
    match variables with
        | [] -> []
        | head :: tail ->
            if containsVariable(head)(environmentVariables)
            then removeEnvironmentVariables(environmentVariables)(tail)
            else head :: removeEnvironmentVariables(environmentVariables)(tail)

let recursive quantifyVariables variables =
    match variables with
        | [] -> []
        | variableId :: tail -> (variableId, "t" + Ashes.Text.fromInt(variableId)) :: quantifyVariables(tail)

let generalize environment semanticType constraints =
    (let environmentVariables = freeEnvironmentVariables(environment)
    in
        let candidateVariables = mergeVariables(freeTypeVariables(semanticType))(freeConstraintsVariables(constraints))
        in
            let generalizedVariables = removeEnvironmentVariables(environmentVariables)(candidateVariables)
            in TypeScheme(quantified = quantifyVariables(generalizedVariables), body = semanticType, constraints = canonicalizeTraitConstraints(constraints)))

let recursive instantiateQuantifiers quantified supply substitution =
    match quantified with
        | [] -> (substitution, supply)
        | (variableId, _name) :: tail ->
            match freshTypeVariable(supply) with
                | (freshVariable, nextSupply) -> instantiateQuantifiers(tail)(nextSupply)((variableId, freshVariable) :: substitution)

let applyConstraintSubstitution substitution constraint =
    match constraint with
        | TraitConstraint { traitName = name, typeArguments = arguments } ->
            let recursive substituteArguments values =
                match values with
                    | [] -> []
                    | head :: tail ->
                        let substitutedHead = applySubstitution(substitution)(head)
                        in
                            let substitutedTail = substituteArguments(tail)
                            in substitutedHead :: substitutedTail
            in TraitConstraint(traitName = name, typeArguments = substituteArguments(arguments))

let recursive applyConstraintSubstitutions substitution constraints =
    match constraints with
        | [] -> []
        | head :: tail ->
            let substitutedHead = applyConstraintSubstitution(substitution)(head)
            in
                let substitutedTail = applyConstraintSubstitutions(substitution)(tail)
                in substitutedHead :: substitutedTail

let instantiate scheme supply =
    match scheme with
        | TypeScheme { quantified = quantified, body = body, constraints = constraints } ->
            match instantiateQuantifiers(quantified)(supply)([]) with
                | (substitution, nextSupply) ->
                    let instantiatedBody = applySubstitution(substitution)(body)
                    in
                        let instantiatedConstraints = canonicalizeTraitConstraints(applyConstraintSubstitutions(substitution)(constraints))
                        in InstantiationResult(semanticType = instantiatedBody, constraints = instantiatedConstraints, supply = nextSupply)
