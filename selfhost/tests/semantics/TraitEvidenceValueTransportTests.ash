import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TraitEvidenceAbi
import AshesCompiler.Semantics.TraitEvidenceThreading
import TraitEvidenceArgumentTests
let equalVariable unit = TraitConstraint(traitName = "Equal", typeArguments = [SemVariable(7)])

let orderedVariable unit = TraitConstraint(traitName = "Ordered", typeArguments = [SemVariable(7)])

let expectDirectFunctionTransport unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planTraitEvidenceValueTransport(
        TraitEvidenceFunctionParameter,
        [equalVariable(Unit)],
        [equalVariable(Unit)]
    ) with
        | TraitEvidenceValueTransportPlanning { transports = TraitEvidenceValueTransport { destination = TraitEvidenceFunctionParameter, shape = TraitDictionaryAbiShape { parameterIndex = 0, constraint = _constraint, methods = _methods, supertraits = _supertraits }, forwarding = TraitEvidenceForwarding { rootParameterIndex = 0, supertraitPath = [] } } :: [], error = None } -> Unit
        | _ -> test.fail("direct constrained calls should transport the exact active dictionary")

let expectInheritedClosureTransport unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planTraitEvidenceValueTransport(TraitEvidenceClosureCapture)([equalVariable(Unit)])([orderedVariable(Unit)]) with
        | TraitEvidenceValueTransportPlanning { transports = TraitEvidenceValueTransport { destination = TraitEvidenceClosureCapture, shape = _shape, forwarding = TraitEvidenceForwarding { rootParameterIndex = 0, supertraitPath = 0 :: [] } } :: [], error = None } -> Unit
        | _ -> test.fail("closures should retain inherited evidence through its supertrait path")

let expectAggregateTransportPath unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planTraitEvidenceValueTransport(
        TraitEvidenceAggregateCapture([1, 0]),
        [equalVariable(Unit)],
        [orderedVariable(Unit)]
    ) with
        | TraitEvidenceValueTransportPlanning { transports = TraitEvidenceValueTransport { destination = TraitEvidenceAggregateCapture(1 :: 0 :: []), shape = _shape, forwarding = TraitEvidenceForwarding { rootParameterIndex = 0, supertraitPath = 0 :: [] } } :: [], error = None } -> Unit
        | _ -> test.fail("aggregate evidence should retain its nested value path")

let expectAsyncFrameTransport unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planTraitEvidenceValueTransport(
        TraitEvidenceAsyncFrameCapture,
        [orderedVariable(Unit)],
        [equalVariable(Unit), orderedVariable(Unit)]
    ) with
        | TraitEvidenceValueTransportPlanning { transports = TraitEvidenceValueTransport { destination = TraitEvidenceAsyncFrameCapture, shape = TraitDictionaryAbiShape { parameterIndex = 0, constraint = TraitConstraint { traitName = "Ordered", typeArguments = SemVariable(7) :: [] }, methods = _methods, supertraits = _supertraits }, forwarding = TraitEvidenceForwarding { rootParameterIndex = 1, supertraitPath = [] } } :: [], error = None } -> Unit
        | _ -> test.fail("async frames should retain the exact active dictionary ABI slot")

let rejectUnavailableValueTransport unit =
    match Unit
    |> TraitEvidenceArgumentTests.evidenceEnvironment
    |> planTraitEvidenceValueTransport(TraitEvidenceClosureCapture)([orderedVariable(Unit)])([equalVariable(Unit)]) with
        | TraitEvidenceValueTransportPlanning { transports = [], error = Some(MissingActiveTraitEvidence(TraitConstraint { traitName = "Ordered", typeArguments = SemVariable(7) :: [] })) } -> Unit
        | _ -> test.fail("value transport should reject unavailable active evidence")

let runTraitEvidenceValueTransportTests unit =
    unit
    |> expectDirectFunctionTransport
    |> expectInheritedClosureTransport
    |> expectAggregateTransportPath
    |> expectAsyncFrameTransport
    |> rejectUnavailableValueTransport
    |> (given (_) -> Ashes.IO.print("all self-hosted trait evidence value transport tests passed"))
