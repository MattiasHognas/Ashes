import Ashes.Test as test
import AshesCompiler.Semantics.Symbols
import AshesCompiler.Semantics.Scope
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
                                                                                in Ashes.IO.print("all self-hosted semantics scope tests passed")
                                                                    | None -> test.fail("nested scope should pop"))

run(Unit)
