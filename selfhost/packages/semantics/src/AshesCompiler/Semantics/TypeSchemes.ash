// Implements free-variable analysis, let generalization, and scheme instantiation.
//
// Invariants:
// - Generalization quantifies only variables not free in the surrounding environment.
// - Every instantiation replaces quantified variables with a fresh, self-contained set.
// - Trait constraints travel with their scheme and remain canonical after substitution.

import AshesCompiler.Semantics.Types
export (
    type InstantiationResult(..),
    value freeTypeVariables,
    value freeSchemeVariables,
    value freeEnvironmentVariables,
    value generalize,
    value generalizeUnderSubstitution,
    value instantiate,
    value instantiatePreservingConstraintOrder,
    value applySchemeSubstitutionMap,
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
        | head :: tail ->
            right
            |> addVariable(head)
            |> mergeVariables(tail)

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
                    in
                        rowVariables
                        |> mergeVariables(resultVariables)
                        |> mergeVariables(argumentVariables)
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
        | head :: tail ->
            tail
            |> freeConstraintsVariables
            |> mergeVariables(freeConstraintVariables(head))

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
            in
                variables
                |> remove
                |> removeQuantified(tail)

let freeSchemeVariables scheme =
    match scheme with
        | TypeScheme { quantified = quantified, body = body, constraints = constraints } ->
            let allVariables =
                constraints
                |> freeConstraintsVariables
                |> mergeVariables(freeTypeVariables(body))
            in removeQuantified(quantified)(allVariables)

let recursive freeEnvironmentVariables environment =
    match environment with
        | [] -> []
        | scheme :: tail ->
            tail
            |> freeEnvironmentVariables
            |> mergeVariables(freeSchemeVariables(scheme))

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

// The environment's free variables are only needed to hold back candidates, so a type with no
// candidate variables never scans the environment.
let generalize environment semanticType constraints =
    (let candidateVariables =
        constraints
        |> freeConstraintsVariables
        |> mergeVariables(freeTypeVariables(semanticType))
    in
        let generalizedVariables =
            match candidateVariables with
                | [] -> []
                | _ ->
                    removeEnvironmentVariables(freeEnvironmentVariables(environment))(candidateVariables)
        in
            TypeScheme(quantified = quantifyVariables(
                generalizedVariables
            ), body = semanticType, constraints = canonicalizeTraitConstraints(constraints)))

let recursive resolveEnvironmentVariables substitution variables resolved =
    match variables with
        | [] -> resolved
        | variable :: rest ->
            resolved
            |> mergeVariables(SemVariable(variable)
            |> applySubstitution(substitution)
            |> freeTypeVariables)
            |> resolveEnvironmentVariables(substitution)(rest)

// A captured monomorphic variable can become an arrow while the binding is inferred.
// Its parameter/result variables still belong to the outer environment. Resolve its free
// variables before excluding them, rather than quantifying those newly exposed variables.
// Removing each scheme's quantified IDs first also preserves independent polymorphic bindings.
// The value type and requirements have already been resolved by the inference boundary.
let generalizeUnderSubstitution environment substitution semanticType constraints =
    (let candidateVariables =
        constraints
        |> freeConstraintsVariables
        |> mergeVariables(freeTypeVariables(semanticType))
    in
        let generalizedVariables =
            match candidateVariables with
                | [] -> []
                | _ ->
                    removeEnvironmentVariables(resolveEnvironmentVariables(substitution)(freeEnvironmentVariables(environment))([]))(candidateVariables)
        in TypeScheme(quantified = quantifyVariables(generalizedVariables), body = semanticType, constraints = canonicalizeTraitConstraints(constraints)))

// Build the whole quantified-variable renaming from one supply before applying it to the body and
// constraints, so separate instantiations cannot accidentally share inference variables.
let recursive instantiateQuantifiers (quantified: List((Int, Str))) (supply: TypeVariableSupply) (substitution: List((Int, SemanticType))) =
    match quantified with
        | [] -> (substitution, supply)
        | (variableId, _name) :: tail ->
            match freshTypeVariable(supply) with
                | (freshVariable, nextSupply) ->
                    instantiateQuantifiers(
                        tail,
                        nextSupply,
                        (variableId, freshVariable) :: substitution
                    )

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
                        let instantiatedConstraints =
                            canonicalizeTraitConstraints(
                                applyConstraintSubstitutions(substitution)(constraints)
                            )
                        in InstantiationResult(semanticType = instantiatedBody, constraints = instantiatedConstraints, supply = nextSupply)

// Runtime dictionary slots are ordered by the definition's scheme. Instantiation
// must rename their types without sorting or coalescing the physical parameters.
let instantiatePreservingConstraintOrder (scheme: TypeScheme) supply =
    match instantiateQuantifiers(scheme.quantified)(supply)([]) with
        | (substitution, nextSupply) -> InstantiationResult(semanticType = applySubstitution(substitution)(scheme.body), constraints = applyConstraintSubstitutions(substitution)(scheme.constraints), supply = nextSupply)

let recursive quantifiedVariableIds quantified =
    match quantified with
        | [] -> []
        | (variable, _name) :: rest -> variable :: quantifiedVariableIds(rest)

// Quantifiers bind their IDs even if the surrounding lowering substitution has
// older entries for those IDs. Preserve constraint order: emitted dictionaries
// already use these positions as their physical ABI.
let applySchemeSubstitutionMap substitution (scheme: TypeScheme) =
    (let bound = quantifiedVariableIds(scheme.quantified)
    in
        let lookup =
            given (variable) ->
                if containsVariable(variable)(bound)
                then None
                else Ashes.Collection.Map.getInt(variable)(substitution)
        in
            scheme with body = applySubstitutionUsing(lookup)(scheme.body), constraints = Ashes.Collection.List.map((given (constraint: TraitConstraint) ->
                constraint with typeArguments = Ashes.Collection.List.map(applySubstitutionUsing(lookup))(constraint.typeArguments)))(scheme.constraints))
