// Plans how trait evidence is supplied, forwarded, and captured across function calls.
//
// Invariants:
// - Exact active evidence is preferred before following deterministic supertrait paths.
// - Partial application accounts for ordinary and hidden evidence arguments separately.
// - A closure captures evidence exactly when unsupplied ordinary arguments keep it live.
// - Value transport retains the destination separately from the evidence source path.

import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TraitResolution
import AshesCompiler.Semantics.TraitEvidenceAbi
import Ashes.Collection.List.reverse
export (
    type TraitEvidenceArgument(..),
    type TraitEvidenceArgumentPlanning(..),
    type TraitFunctionApplicationError(..),
    type TraitFunctionApplicationPlanning(..),
    type TraitEvidenceForwarding(..),
    type TraitEvidenceForwardingArgument(..),
    type TraitEvidenceForwardingError(..),
    type TraitEvidenceForwardingPlanning(..),
    type TraitMethodAccess(..),
    type TraitMethodAccessError(..),
    type TraitMethodAccessPlanning(..),
    type TraitEvidenceValueDestination(..),
    type TraitEvidenceValueTransport(..),
    type TraitEvidenceValueTransportPlanning(..),
    value planTraitEvidenceArguments,
    value planTraitFunctionApplication,
    value planTraitEvidenceForwarding,
    value planActiveTraitMethodAccess,
    value planTraitEvidenceValueTransport,
)

type TraitEvidenceArgument =
    | shape: TraitDictionaryAbiShape
    | evidence: TraitEvidencePlan

type TraitEvidenceArgumentPlanning =
    | arguments: List(TraitEvidenceArgument)
    | error: Maybe(TraitResolutionError)

type TraitFunctionApplicationError =
    | TraitFunctionEvidenceResolution(TraitResolutionError)
    | TraitFunctionOverapplication(Int, Int)
    | TraitFunctionInvalidArgumentCount(Int)
    deriving {Eq, Show}

type TraitFunctionApplicationPlanning =
    | evidenceArguments: List(TraitEvidenceArgument)
    | ordinaryArity: Int
    | suppliedOrdinaryArguments: Int
    | remainingOrdinaryArguments: Int
    | capturesEvidence: Bool
    | error: Maybe(TraitFunctionApplicationError)

type TraitEvidenceForwarding =
    | rootParameterIndex: Int
    | supertraitPath: List(Int)
    deriving {Eq, Show}

type TraitEvidenceForwardingArgument =
    | shape: TraitDictionaryAbiShape
    | forwarding: TraitEvidenceForwarding
    deriving {Eq, Show}

type TraitEvidenceForwardingError =
    | MissingActiveTraitEvidence(TraitConstraint)
    deriving {Eq, Show}

type TraitEvidenceForwardingPlanning =
    | arguments: List(TraitEvidenceForwardingArgument)
    | error: Maybe(TraitEvidenceForwardingError)
    deriving {Eq, Show}

type TraitMethodAccess =
    | forwarding: TraitEvidenceForwarding
    | methodName: Str
    | methodIndex: Int
    deriving {Eq, Show}

type TraitMethodAccessError =
    | TraitMethodEvidenceUnavailable(TraitConstraint)
    | TraitMethodNotInDictionary(TraitConstraint, Str)
    deriving {Eq, Show}

type TraitMethodAccessPlanning =
    | access: Maybe(TraitMethodAccess)
    | error: Maybe(TraitMethodAccessError)
    deriving {Eq, Show}

type TraitEvidenceValueDestination =
    | TraitEvidenceFunctionParameter
    | TraitEvidenceClosureCapture
    | TraitEvidenceAggregateCapture(List(Int))
    | TraitEvidenceAsyncFrameCapture
    deriving {Eq, Show}

type TraitEvidenceValueTransport =
    | destination: TraitEvidenceValueDestination
    | shape: TraitDictionaryAbiShape
    | forwarding: TraitEvidenceForwarding
    deriving {Eq, Show}

type TraitEvidenceValueTransportPlanning =
    | transports: List(TraitEvidenceValueTransport)
    | error: Maybe(TraitEvidenceForwardingError)
    deriving {Eq, Show}

let recursive planTraitEvidenceArgumentsFrom shapes environment reversed =
    match shapes with
        | [] -> TraitEvidenceArgumentPlanning(arguments = reverse(reversed), error = None)
        | (TraitDictionaryAbiShape { parameterIndex = _parameterIndex, constraint = constraint, methods = _methods, supertraits = _supertraits } as shape) :: tail ->
            match resolveTraitEvidence(constraint)(environment) with
                | TraitEvidenceResolution { plan = Some(evidence), error = None } ->
                    planTraitEvidenceArgumentsFrom(
                        tail,
                        environment,
                        TraitEvidenceArgument(shape = shape, evidence = evidence) :: reversed
                    )
                | TraitEvidenceResolution { plan = _plan, error = Some(error) } ->
                    TraitEvidenceArgumentPlanning(arguments = reverse(
                        reversed
                    ), error = Some(error))
                | _ ->
                    TraitEvidenceArgumentPlanning(arguments = reverse(reversed), error = Some(
                        MissingTraitImplementation(constraint)([constraint])
                    ))

let planTraitEvidenceArguments constraints environment =
    planTraitEvidenceArgumentsFrom(
        planTraitEvidenceAbi(constraints)(environment),
        environment,
        []
    )

let recursive traitFunctionOrdinaryArity semanticType =
    match semanticType with
        | SemFunction(_argument, result, _capabilityRow) -> 1 + traitFunctionOrdinaryArity(result)
        | _ -> 0

let traitEvidenceArgumentsPresent arguments =
    match arguments with
        | [] -> false
        | _head :: _tail -> true

let traitFunctionApplicationFailure ordinaryArity supplied error =
    TraitFunctionApplicationPlanning(evidenceArguments = [], ordinaryArity = ordinaryArity, suppliedOrdinaryArguments = supplied, remainingOrdinaryArguments = 0, capturesEvidence = false, error = Some(
        error
    ))

let traitFunctionCapturesEvidence remaining arguments =
    if remaining > 0
    then traitEvidenceArgumentsPresent(arguments)
    else false

let planResolvedTraitFunctionApplication ordinaryArity suppliedOrdinaryArguments arguments =
    TraitFunctionApplicationPlanning(evidenceArguments = arguments, ordinaryArity = ordinaryArity, suppliedOrdinaryArguments = suppliedOrdinaryArguments, remainingOrdinaryArguments = ordinaryArity - suppliedOrdinaryArguments, capturesEvidence = traitFunctionCapturesEvidence(
        ordinaryArity - suppliedOrdinaryArguments,
        arguments
    ), error = None)

let planTraitFunctionApplicationWithArity ordinaryArity suppliedOrdinaryArguments constraints environment =
    if suppliedOrdinaryArguments < 0
    then
        traitFunctionApplicationFailure(
            ordinaryArity,
            suppliedOrdinaryArguments,
            TraitFunctionInvalidArgumentCount(suppliedOrdinaryArguments)
        )
    else
        if suppliedOrdinaryArguments > ordinaryArity
        then
            traitFunctionApplicationFailure(
                ordinaryArity,
                suppliedOrdinaryArguments,
                TraitFunctionOverapplication(ordinaryArity)(suppliedOrdinaryArguments)
            )
        else
            match planTraitEvidenceArguments(constraints)(environment) with
                | TraitEvidenceArgumentPlanning { arguments = arguments, error = Some(error) } ->
                    TraitFunctionApplicationPlanning(evidenceArguments = arguments, ordinaryArity = ordinaryArity, suppliedOrdinaryArguments = suppliedOrdinaryArguments, remainingOrdinaryArguments = ordinaryArity - suppliedOrdinaryArguments, capturesEvidence = false, error = Some(
                        TraitFunctionEvidenceResolution(error)
                    ))
                | TraitEvidenceArgumentPlanning { arguments = arguments, error = None } ->
                    planResolvedTraitFunctionApplication(
                        ordinaryArity,
                        suppliedOrdinaryArguments,
                        arguments
                    )

let planTraitFunctionApplication scheme suppliedOrdinaryArguments environment =
    match scheme with
        | TypeScheme { quantified = _quantified, body = body, constraints = constraints } ->
            planTraitFunctionApplicationWithArity(
                traitFunctionOrdinaryArity(body),
                suppliedOrdinaryArguments,
                constraints,
                environment
            )

let traitEvidenceShapeMatches target shape =
    match shape with
        | TraitDictionaryAbiShape { parameterIndex = _parameterIndex, constraint = constraint, methods = _methods, supertraits = _supertraits } ->
            traitConstraintStableKey(
                target
            ) == traitConstraintStableKey(
                constraint
            )

let recursive findTraitEvidenceForwardingInSupertraits shapes target rootParameterIndex reversedPath ordinal =
    match shapes with
        | [] -> None
        | head :: tail ->
            match findTraitEvidenceForwardingInShape(head)(target)(rootParameterIndex)(ordinal :: reversedPath) with
                | Some(forwarding) -> Some(forwarding)
                | None ->
                    findTraitEvidenceForwardingInSupertraits(
                        tail,
                        target,
                        rootParameterIndex,
                        reversedPath,
                        ordinal + 1
                    )
and findTraitEvidenceForwardingInShape shape target rootParameterIndex reversedPath =
    if traitEvidenceShapeMatches(target)(shape)
    then Some(TraitEvidenceForwarding(rootParameterIndex = rootParameterIndex, supertraitPath = reverse(reversedPath)))
    else
        match shape with
            | TraitDictionaryAbiShape { parameterIndex = _parameterIndex, constraint = _constraint, methods = _methods, supertraits = supertraits } ->
                findTraitEvidenceForwardingInSupertraits(
                    supertraits,
                    target,
                    rootParameterIndex,
                    reversedPath,
                    0
                )

let recursive findTraitEvidenceForwarding shapes target =
    match shapes with
        | [] -> None
        | (TraitDictionaryAbiShape { parameterIndex = parameterIndex, constraint = _constraint, methods = _methods, supertraits = _supertraits } as shape) :: tail ->
            match findTraitEvidenceForwardingInShape(shape)(target)(parameterIndex)([]) with
                | Some(forwarding) -> Some(forwarding)
                | None -> findTraitEvidenceForwarding(tail)(target)

let recursive isFreeVariableSemanticType semanticType =
    match semanticType with
        | SemVariable(_variableId) -> true
        | _ -> false

let recursive allFreeVariableSemanticTypes types =
    match types with
        | [] -> true
        | head :: tail ->
            if isFreeVariableSemanticType(head)
            then allFreeVariableSemanticTypes(tail)
            else false

// A constraint whose type argument(s) are still free variables — unification hasn't yet identified
// them with anything concrete, so exact stable-key matching (traitEvidenceShapeMatches) can never
// hit, even against an active dictionary parameter for the exact same underlying obligation: each
// side's variable was independently instantiated fresh and has no reason to share an id.
let constraintIsStillAbstract constraint =
    match constraint with
        | TraitConstraint { traitName = _traitName, typeArguments = typeArguments } -> allFreeVariableSemanticTypes(typeArguments)

let sameTraitName target shape =
    match (target, shape) with
        | (TraitConstraint { traitName = targetName, typeArguments = _targetTypeArguments }, TraitDictionaryAbiShape { parameterIndex = _parameterIndex, constraint = TraitConstraint { traitName = shapeName, typeArguments = _shapeTypeArguments }, methods = _methods, supertraits = _supertraits }) -> targetName == shapeName

let recursive countShapesWithTraitName shapes target =
    match shapes with
        | [] -> 0
        | shape :: rest ->
            if sameTraitName(target)(shape)
            then 1 + countShapesWithTraitName(rest)(target)
            else countShapesWithTraitName(rest)(target)

let recursive findShapeByTraitName shapes target =
    match shapes with
        | [] -> None
        | (TraitDictionaryAbiShape { parameterIndex = parameterIndex, constraint = _constraint, methods = _methods, supertraits = _supertraits } as shape) :: rest ->
            if sameTraitName(target)(shape)
            then Some(TraitEvidenceForwarding(rootParameterIndex = parameterIndex, supertraitPath = []))
            else findShapeByTraitName(rest)(target)

// The hidden dictionary parameter in scope that supplies `target`: the one bound to the exact same
// pruned constraint (following supertrait paths), or — unless the requirement is concrete — the
// sole top-level active parameter for that trait, when inference has not yet identified its type
// variable with the parameter's. Sound only because it's the ONLY option: with two or more active
// parameters for the same trait, which one is right depends on type information this check doesn't
// have, so no fallback fires and the caller's own error path (missing evidence) takes over. Mirrors
// stage-0's FindActiveTraitDictionaryParameter fallback (Lowering.TraitEvidence.cs).
let findTraitEvidenceForwardingWithFallback shapes target =
    match findTraitEvidenceForwarding(shapes)(target) with
        | Some(forwarding) -> Some(forwarding)
        | None ->
            if constraintIsStillAbstract(target)
            then
                if countShapesWithTraitName(shapes)(target) == 1
                then findShapeByTraitName(shapes)(target)
                else None
            else None

let recursive planTraitEvidenceForwardingFrom requiredShapes activeShapes reversed =
    match requiredShapes with
        | [] -> TraitEvidenceForwardingPlanning(arguments = reverse(reversed), error = None)
        | (TraitDictionaryAbiShape { parameterIndex = _parameterIndex, constraint = constraint, methods = _methods, supertraits = _supertraits } as shape) :: tail ->
            match findTraitEvidenceForwardingWithFallback(activeShapes)(constraint) with
                | Some(forwarding) ->
                    planTraitEvidenceForwardingFrom(
                        tail,
                        activeShapes,
                        TraitEvidenceForwardingArgument(shape = shape, forwarding = forwarding) :: reversed
                    )
                | None ->
                    TraitEvidenceForwardingPlanning(arguments = reverse(reversed), error = Some(
                        MissingActiveTraitEvidence(constraint)
                    ))

let planTraitEvidenceForwarding requiredConstraints activeConstraints environment =
    planTraitEvidenceForwardingFrom(
        planTraitEvidenceAbi(requiredConstraints)(environment),
        planTraitEvidenceAbi(activeConstraints)(environment),
        []
    )

let recursive attachTraitEvidenceDestination destination arguments =
    match arguments with
        | [] -> []
        | TraitEvidenceForwardingArgument { shape = shape, forwarding = forwarding } :: tail ->
            TraitEvidenceValueTransport(destination = destination, shape = shape, forwarding = forwarding) :: attachTraitEvidenceDestination(
                destination,
                tail
            )

let planTraitEvidenceValueTransport destination requiredConstraints activeConstraints environment =
    match planTraitEvidenceForwarding(requiredConstraints)(activeConstraints)(environment) with
        | TraitEvidenceForwardingPlanning { arguments = arguments, error = error } ->
            TraitEvidenceValueTransportPlanning(transports = attachTraitEvidenceDestination(
                destination,
                arguments
            ), error = error)

let recursive findTraitEvidenceShapeInSupertraits shapes target =
    match shapes with
        | [] -> None
        | head :: tail ->
            match findTraitEvidenceShapeInShape(head)(target) with
                | Some(shape) -> Some(shape)
                | None -> findTraitEvidenceShapeInSupertraits(tail)(target)
and findTraitEvidenceShapeInShape shape target =
    if traitEvidenceShapeMatches(target)(shape)
    then Some(shape)
    else
        match shape with
            | TraitDictionaryAbiShape { parameterIndex = _parameterIndex, constraint = _constraint, methods = _methods, supertraits = supertraits } ->
                findTraitEvidenceShapeInSupertraits(
                    supertraits,
                    target
                )

let recursive findTraitEvidenceShape shapes target =
    match shapes with
        | [] -> None
        | head :: tail ->
            match findTraitEvidenceShapeInShape(head)(target) with
                | Some(shape) -> Some(shape)
                | None -> findTraitEvidenceShape(tail)(target)

let recursive findTraitMethodIndex methods methodName index =
    match methods with
        | [] -> None
        | head :: tail ->
            if head == methodName
            then Some(index)
            else findTraitMethodIndex(tail)(methodName)(index + 1)

let planActiveTraitMethodAccessFrom shape forwarding constraint methodName =
    match shape with
        | TraitDictionaryAbiShape { parameterIndex = _parameterIndex, constraint = _shapeConstraint, methods = methods, supertraits = _supertraits } ->
            match findTraitMethodIndex(methods)(methodName)(0) with
                | Some(methodIndex) ->
                    TraitMethodAccessPlanning(access = Some(
                        TraitMethodAccess(forwarding = forwarding, methodName = methodName, methodIndex = methodIndex)
                    ), error = None)
                | None ->
                    TraitMethodAccessPlanning(access = None, error = Some(
                        TraitMethodNotInDictionary(constraint)(methodName)
                    ))

let planActiveTraitMethodAccessWithShapes constraint methodName activeShapes =
    match findTraitEvidenceForwarding(activeShapes)(constraint) with
        | None -> TraitMethodAccessPlanning(access = None, error = Some(TraitMethodEvidenceUnavailable(constraint)))
        | Some(forwarding) ->
            match findTraitEvidenceShape(activeShapes)(constraint) with
                | Some(shape) -> planActiveTraitMethodAccessFrom(shape)(forwarding)(constraint)(methodName)
                | None ->
                    TraitMethodAccessPlanning(access = None, error = Some(
                        TraitMethodEvidenceUnavailable(constraint)
                    ))

let planActiveTraitMethodAccess constraint methodName activeConstraints environment =
    planActiveTraitMethodAccessWithShapes(
        constraint,
        methodName,
        planTraitEvidenceAbi(activeConstraints)(environment)
    )
