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
import ProjectManifestTests
import ProjectDiscoveryTests
import ProjectSourceEnumerationTests
import ProjectCompilationPlanningTests
let declared result =
    match result with
        | DeclarationResult { context = context, symbol = Some(symbol), duplicate = None } -> (context, symbol)
        | _ -> test.fail("declaration should succeed")

let symbolId symbol =
    match symbol with
        | SemanticSymbol { id = id, name = _name, qualifiedName = _qualifiedName, kind = _kind, definitionSpan = _span } -> id

let symbolName symbol =
    match symbol with
        | SemanticSymbol { id = _id, name = name, qualifiedName = _qualifiedName, kind = _kind, definitionSpan = _span } -> name

let expectResolved expectedId result =
    match result with
        | Some(symbol) ->
            symbol
            |> symbolId
            |> test.assertEqual(expectedId)
        | None -> test.fail("symbol should resolve")

let rejectDuplicate shadowed =
    match declare("value")(None)(SymbolType)(None)(shadowed) with
        | DeclarationResult { context = unchanged, symbol = None, duplicate = Some(existing) } ->
            existing
            |> symbolId
            |> test.assertEqual(1)
            |> (given (_) ->
                unchanged
                |> scopeDepth
                |> test.assertEqual(1))
        | _ -> test.fail("same-frame declaration should be rejected")

let expectRestoredScope restored =
    restored
    |> resolve("value")
    |> expectResolved(0)
    |> (given (_) ->
        match resolve("missing")(restored) with
            | None -> Unit
            | Some(_) -> test.fail("missing symbol should not resolve"))
    |> (given (_) ->
        match leaveScope(restored) with
            | None -> Unit
            | Some(_) -> test.fail("root scope should not pop"))

let expectScopeBehavior unit =
    match Unit
    |> createContext
    |> declare("value")(Some("Main.value"))(SymbolValue)(None)
    |> declared with
        | (withValue, valueSymbol) ->
            match withValue
            |> enterScope
            |> declare("value")(None)(SymbolValue)(None)
            |> declared with
                | (shadowed, shadowSymbol) ->
                    valueSymbol
                    |> symbolId
                    |> test.assertEqual(0)
                    |> (given (_) ->
                        withValue
                        |> resolve("value")
                        |> expectResolved(0))
                    |> (given (_) ->
                        withValue
                        |> resolveQualified("Main.value")
                        |> expectResolved(0))
                    |> (given (_) ->
                        shadowSymbol
                        |> symbolId
                        |> test.assertEqual(1))
                    |> (given (_) ->
                        shadowed
                        |> resolve("value")
                        |> expectResolved(1))
                    |> (given (_) ->
                        shadowed
                        |> scopeDepth
                        |> test.assertEqual(1))
                    |> (given (_) -> rejectDuplicate(shadowed))
                    |> (given (_) ->
                        match leaveScope(shadowed) with
                            | Some(restored) -> expectRestoredScope(restored)
                            | None -> test.fail("nested scope should pop"))

let expectFreshTypeVariables unit =
    match Unit
    |> initialTypeVariableSupply
    |> freshTypeVariable with
        | (firstVariable, afterFirst) ->
            match freshTypeVariable(afterFirst) with
                | (secondVariable, _afterSecond) ->
                    ((given (_) ->
                        match secondVariable with
                            | SemVariable(1) -> Unit
                            | _ -> test.fail("second type variable should have id one")))(match firstVariable with
                        | SemVariable(0) -> Unit
                        | _ -> test.fail("first type variable should have id zero"))

let expectTypeOperations unit =
    (let polymorphic =
        SemFunction(SemVariable(0))(SemList(SemVariable(1)))(Some(SemVariable(2))
        |> SemRow([SemCapability("State")([SemVariable(0)])])
        |> Some)
    in
        (if occursInType(2)(polymorphic)
        then Unit
        else test.fail("row-tail variable should occur"))
        |> (given (_) ->
            if occursInType(9)(polymorphic)
            then test.fail("unrelated variable should not occur")
            else Unit)
        |> (given (_) ->
            let actual =
                polymorphic
                |> applySubstitution([(0, SemInt), (1, SemString), (2, SemRow([])(None))])
                |> formatSemanticType
            in test.assertEqual("Int -> List(Str) needs {State(Int) | {}}")(actual)))

let run unit =
    unit
    |> expectScopeBehavior
    |> expectFreshTypeVariables
    |> expectTypeOperations
    |> UnificationTests.runUnificationTests
    |> TypeSchemeTests.runTypeSchemeTests
    |> TypeInferenceTests.runTypeInferenceTests
    |> PatternInferenceTests.runPatternInferenceTests
    |> TypeResolutionTests.runTypeResolutionTests
    |> ProgramInferenceTests.runProgramInferenceTests
    |> RecordInferenceTests.runRecordInferenceTests
    |> ResultInferenceTests.runResultInferenceTests
    |> CapabilityInferenceTests.runCapabilityInferenceTests
    |> TraitInferenceTests.runTraitInferenceTests
    |> TraitEvidenceAbiTests.runTraitEvidenceAbiTests
    |> TraitEvidenceArgumentTests.runTraitEvidenceArgumentTests
    |> TraitEvidenceApplicationTests.runTraitEvidenceApplicationTests
    |> TraitEvidenceForwardingTests.runTraitEvidenceForwardingTests
    |> TraitMethodAccessTests.runTraitMethodAccessTests
    |> TraitDictionaryConstructionTests.runTraitDictionaryConstructionTests
    |> TraitMethodConstructionOrderTests.runTraitMethodConstructionOrderTests
    |> TraitEvidenceValueTransportTests.runTraitEvidenceValueTransportTests
    |> ProjectManifestTests.run
    |> ProjectDiscoveryTests.runProjectDiscoveryTests
    |> ProjectSourceEnumerationTests.runProjectSourceEnumerationTests
    |> ProjectCompilationPlanningTests.runProjectCompilationPlanningTests
    |> (given (_) -> Ashes.IO.print("all self-hosted semantics foundation tests passed"))

run(Unit)
