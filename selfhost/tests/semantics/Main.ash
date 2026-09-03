import Ashes.Test as test
import AshesCompiler.Semantics.Symbols
import AshesCompiler.Semantics.Scope
import AshesCompiler.Semantics.Types
import UnificationTests
import TypeSchemeTests
import TypeInferenceTests
import PatternInferenceTests
import TypeResolutionTests
import ProgramInferenceTests
import RecordInferenceTests
import ResultInferenceTests
import CapabilityInferenceTests
import TraitInferenceTests
import TraitEvidenceAbiTests
import TraitEvidenceArgumentTests
import TraitEvidenceApplicationTests
import TraitEvidenceForwardingTests
import TraitMethodAccessTests
import TraitDictionaryConstructionTests
import TraitMethodConstructionOrderTests
import TraitEvidenceValueTransportTests
import TraitEvidenceRewritingTests
import TraitEvidenceCallRewritingTests
import TraitDictionaryRewritingTests
import StandardTraitsTests
import StandardTraitSourceBindingTests
import DerivingExpansionTests
import ExternalTypingTests
import ExternalAbiTests
import IrTests
import IrFunctionSelectionTests
import IrTextTests
import CoreLoweringTests
import CoreProgramLoweringTests
import CoreBuiltinLoweringTests
import CoreExternalLoweringTests
import CoreCapabilityLoweringTests
import StateMachineTransformTests
import MetadataAndOriginsTests
import TcoTests
import IrValidationTests
import IrOptimizerTests
import PerceusLifetimePlacementTests
import OwnershipInferenceTests
import HeapLayoutClassificationTests
import ExplainReportTests
import TaglessAdtLayoutTests
import StructuralDroppersTests
import ResultReachTests
let run unit =
    Unit
    |> UnificationTests.runUnificationTests
    |> (given (_) -> TypeSchemeTests.runTypeSchemeTests(Unit))
    |> (given (_) -> TypeInferenceTests.runTypeInferenceTests(Unit))
    |> (given (_) -> PatternInferenceTests.runPatternInferenceTests(Unit))
    |> (given (_) -> TypeResolutionTests.runTypeResolutionTests(Unit))
    |> (given (_) -> ProgramInferenceTests.runProgramInferenceTests(Unit))
    |> (given (_) -> RecordInferenceTests.runRecordInferenceTests(Unit))
    |> (given (_) -> ResultInferenceTests.runResultInferenceTests(Unit))
    |> (given (_) -> CapabilityInferenceTests.runCapabilityInferenceTests(Unit))
    |> (given (_) -> TraitInferenceTests.runTraitInferenceTests(Unit))
    |> (given (_) -> TraitEvidenceAbiTests.runTraitEvidenceAbiTests(Unit))
    |> (given (_) -> TraitEvidenceArgumentTests.runTraitEvidenceArgumentTests(Unit))
    |> (given (_) -> TraitEvidenceApplicationTests.runTraitEvidenceApplicationTests(Unit))
    |> (given (_) -> TraitEvidenceForwardingTests.runTraitEvidenceForwardingTests(Unit))
    |> (given (_) -> TraitMethodAccessTests.runTraitMethodAccessTests(Unit))
    |> (given (_) -> TraitDictionaryConstructionTests.runTraitDictionaryConstructionTests(Unit))
    |> (given (_) -> TraitMethodConstructionOrderTests.runTraitMethodConstructionOrderTests(Unit))
    |> (given (_) -> TraitEvidenceValueTransportTests.runTraitEvidenceValueTransportTests(Unit))
    |> (given (_) -> TraitEvidenceRewritingTests.runTraitEvidenceRewritingTests(Unit))
    |> (given (_) -> TraitEvidenceCallRewritingTests.runTraitEvidenceCallRewritingTests(Unit))
    |> (given (_) -> TraitDictionaryRewritingTests.runTraitDictionaryRewritingTests(Unit))
    |> (given (_) -> StandardTraitsTests.runStandardTraitsTests(Unit))
    |> (given (_) -> StandardTraitSourceBindingTests.runStandardTraitSourceBindingTests(Unit))
    |> (given (_) -> DerivingExpansionTests.runDerivingExpansionTests(Unit))
    |> (given (_) -> ExternalTypingTests.runExternalTypingTests(Unit))
    |> (given (_) -> ExternalAbiTests.runExternalAbiTests(Unit))
    |> (given (_) -> IrTests.runIrTests(Unit))
    |> (given (_) -> IrFunctionSelectionTests.runIrFunctionSelectionTests(Unit))
    |> (given (_) -> IrTextTests.runIrTextTests(Unit))
    |> (given (_) -> CoreLoweringTests.runCoreLoweringTests(Unit))
    |> (given (_) -> CoreProgramLoweringTests.runCoreProgramLoweringTests(Unit))
    |> (given (_) -> CoreBuiltinLoweringTests.runCoreBuiltinLoweringTests(Unit))
    |> (given (_) -> CoreExternalLoweringTests.runCoreExternalLoweringTests(Unit))
    |> (given (_) -> CoreCapabilityLoweringTests.runCoreCapabilityLoweringTests(Unit))
    |> (given (_) -> StateMachineTransformTests.runStateMachineTransformTests(Unit))
    |> (given (_) -> MetadataAndOriginsTests.runMetadataAndOriginsTests(Unit))
    |> (given (_) -> TcoTests.runTcoTests(Unit))
    |> (given (_) -> IrValidationTests.runIrValidationTests(Unit))
    |> (given (_) -> IrOptimizerTests.runIrOptimizerTests(Unit))
    |> (given (_) -> PerceusLifetimePlacementTests.runPerceusLifetimePlacementTests(Unit))
    |> (given (_) -> OwnershipInferenceTests.runOwnershipInferenceTests(Unit))
    |> (given (_) -> HeapLayoutClassificationTests.runHeapLayoutClassificationTests(Unit))
    |> (given (_) -> ExplainReportTests.runExplainReportTests(Unit))
    |> (given (_) -> TaglessAdtLayoutTests.runTaglessAdtLayoutTests(Unit))
    |> (given (_) -> StructuralDroppersTests.runStructuralDroppersTests(Unit))
    |> (given (_) -> ResultReachTests.runResultReachTests(Unit))
    |> (given (_) -> Ashes.IO.print("all semantics core and tco tests passed"))

run(Unit)
