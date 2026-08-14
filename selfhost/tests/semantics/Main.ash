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
        | Some(symbol) -> test.assertEqual(expectedId)(symbolId(symbol))
        | None -> test.fail("symbol should resolve")

let run unit =
    (let root = createContext(Unit)
    in
        let firstDeclaration = declare("value")(Some("Main.value"))(SymbolValue)(None)(root)
        in
            match declared(firstDeclaration) with
                | (withValue, valueSymbol) ->
                    let stableIdChecked = test.assertEqual(0)(symbolId(valueSymbol))
                    in
                        let rootResolutionChecked = expectResolved(0)(resolve("value")(withValue))
                        in
                            let qualifiedResolutionChecked = expectResolved(0)(resolveQualified("Main.value")(withValue))
                            in
                                let nested = enterScope(withValue)
                                in
                                    match declared(declare("value")(None)(SymbolValue)(None)(nested)) with
                                        | (shadowed, shadowSymbol) ->
                                            let shadowIdChecked = test.assertEqual(1)(symbolId(shadowSymbol))
                                            in
                                                let shadowResolutionChecked = expectResolved(1)(resolve("value")(shadowed))
                                                in
                                                    let depthChecked = test.assertEqual(1)(scopeDepth(shadowed))
                                                    in
                                                        let duplicate = declare("value")(None)(SymbolType)(None)(shadowed)
                                                        in
                                                            let duplicateChecked =
                                                                match duplicate with
                                                                    | DeclarationResult { context = unchanged, symbol = None, duplicate = Some(existing) } ->
                                                                        let idChecked = test.assertEqual(1)(symbolId(existing))
                                                                        in test.assertEqual(1)(scopeDepth(unchanged))
                                                                    | _ -> test.fail("same-frame declaration should be rejected")
                                                            in
                                                                match leaveScope(shadowed) with
                                                                    | Some(restored) ->
                                                                        let restoredChecked = expectResolved(0)(resolve("value")(restored))
                                                                        in
                                                                            let missingChecked =
                                                                                match resolve("missing")(restored) with
                                                                                    | None -> Unit
                                                                                    | Some(_) -> test.fail("missing symbol should not resolve")
                                                                            in
                                                                                let rootCannotPopChecked =
                                                                                    match leaveScope(restored) with
                                                                                        | None -> Unit
                                                                                        | Some(_) -> test.fail("root scope should not pop")
                                                                                in
                                                                                    match freshTypeVariable(initialTypeVariableSupply(Unit)) with
                                                                                        | (firstVariable, afterFirst) ->
                                                                                            let firstChecked =
                                                                                                match firstVariable with
                                                                                                    | SemVariable(0) -> Unit
                                                                                                    | _ -> test.fail("first type variable should have id zero")
                                                                                            in
                                                                                                match freshTypeVariable(afterFirst) with
                                                                                                    | (secondVariable, _afterSecond) ->
                                                                                                        let secondChecked =
                                                                                                            match secondVariable with
                                                                                                                | SemVariable(1) -> Unit
                                                                                                                | _ -> test.fail("second type variable should have id one")
                                                                                                        in
                                                                                                            let polymorphic = SemFunction(SemVariable(0))(SemList(SemVariable(1)))(Some(SemRow([SemCapability("State")([SemVariable(0)])])(Some(SemVariable(2)))))
                                                                                                            in
                                                                                                                let occursChecked =
                                                                                                                    if occursInType(2)(polymorphic)
                                                                                                                    then Unit
                                                                                                                    else test.fail("row-tail variable should occur")
                                                                                                                in
                                                                                                                    let absentChecked =
                                                                                                                        if occursInType(9)(polymorphic)
                                                                                                                        then test.fail("unrelated variable should not occur")
                                                                                                                        else Unit
                                                                                                                    in
                                                                                                                        let substituted = applySubstitution([(0, SemInt), (1, SemString), (2, SemRow([])(None))])(polymorphic)
                                                                                                                        in
                                                                                                                            let formatted = formatSemanticType(substituted)
                                                                                                                            in
                                                                                                                                if formatted == "Int -> List(Str) needs {State(Int) | {}}"
                                                                                                                                then
                                                                                                                                    let unificationChecked = UnificationTests.runUnificationTests(Unit)
                                                                                                                                    in
                                                                                                                                        let schemesChecked = TypeSchemeTests.runTypeSchemeTests(Unit)
                                                                                                                                        in
                                                                                                                                            let inferenceChecked = TypeInferenceTests.runTypeInferenceTests(Unit)
                                                                                                                                            in
                                                                                                                                                let patternInferenceChecked = PatternInferenceTests.runPatternInferenceTests(Unit)
                                                                                                                                                in
                                                                                                                                                    let typeResolutionChecked = TypeResolutionTests.runTypeResolutionTests(Unit)
                                                                                                                                                    in
                                                                                                                                                        let programInferenceChecked = ProgramInferenceTests.runProgramInferenceTests(Unit)
                                                                                                                                                        in
                                                                                                                                                            let recordInferenceChecked = RecordInferenceTests.runRecordInferenceTests(Unit)
                                                                                                                                                            in Ashes.IO.print("all self-hosted semantics foundation tests passed")
                                                                                                                                else test.fail("unexpected substituted type: " + formatted)
                                                                    | None -> test.fail("nested scope should pop"))

run(Unit)
