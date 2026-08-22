// Tests source maps, definition/hover identities, diagnostic locations, function origins, and explanation metadata.

import Ashes.Collection.List.length
import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.CoroutineFrame
import AshesCompiler.Semantics.DecisionSnapshot
import AshesCompiler.Semantics.FunctionOrigins
import AshesCompiler.Semantics.HoverTypeInfo
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.SourceContext
import AshesCompiler.Semantics.Types
export (
    value runMetadataAndOriginsTests,
)

let testSourceContextSingleFile unit =
    (let source = "let add x y =\n    x + y\n\nadd(1)(2)"
    in
        let ctx = createSourceContext("/path/to/test.ash")(source)
        in
            let loc0 = resolveOffsetLocation(ctx)(0)
            in
                let _ =
                    match loc0 with
                        | None -> test.fail("expected location at offset 0")
                        | Some(IrSourceLocation(path, line, col)) ->
                            let _ = test.assertEqual("/path/to/test.ash")(path)
                            in
                                let _ = test.assertEqual(1)(line)
                                in
                                    let _ = test.assertEqual(1)(col)
                                    in Unit
                in
                    let loc18 = resolveOffsetLocation(ctx)(18)
                    in
                        let _ =
                            match loc18 with
                                | None -> test.fail("expected location at offset 18")
                                | Some(IrSourceLocation(_p, l2, c2)) ->
                                    let _ = test.assertEqual(2)(l2)
                                    in
                                        let _ = test.assertEqual(5)(c2)
                                        in Unit
                        in Unit)

let testSourceContextMultiFile unit =
    (let combinedSource = "let x = 1\nlet y = 2\nlet entry = x + y"
    in
        let regions =
            [
                ModuleRegion(regionFilePath = "ModuleA.ash", startOffset = 0, endOffset = 10),
                ModuleRegion(regionFilePath = "ModuleB.ash", startOffset = 10, endOffset = 20),
                ModuleRegion(regionFilePath = "Main.ash", startOffset = 20, endOffset = 37)
            ]
        in
            let ctx = createMultiFileSourceContext(combinedSource)(regions)("Main.ash")
            in
                let locA = resolveOffsetLocation(ctx)(4)
                in
                    let _ =
                        match locA with
                            | None -> test.fail("expected location in ModuleA")
                            | Some(IrSourceLocation(path, line, col)) ->
                                let _ = test.assertEqual("ModuleA.ash")(path)
                                in
                                    let _ = test.assertEqual(1)(line)
                                    in
                                        let _ = test.assertEqual(5)(col)
                                        in Unit
                    in
                        let locB = resolveOffsetLocation(ctx)(14)
                        in
                            let _ =
                                match locB with
                                    | None -> test.fail("expected location in ModuleB")
                                    | Some(IrSourceLocation(path, line, col)) ->
                                        let _ = test.assertEqual("ModuleB.ash")(path)
                                        in
                                            let _ = test.assertEqual(1)(line)
                                            in
                                                let _ = test.assertEqual(5)(col)
                                                in Unit
                            in
                                let locOut = resolveOffsetLocation(ctx)(100)
                                in
                                    let _ = test.assertEqual(true)(locOut == None)
                                    in Unit)

let testRuntimeMachineryTagging unit =
    (let ctx = createSourceContext("Test.ash")("let v = 42")
    in
        let span = Some(TextSpan(4)(5))
        in
            let returnInst = tagInstruction(Return(0))(span)(Some(ctx))
            in
                let _ =
                    match returnInst with
                        | IrInstruction(Return(temp), Some(IrSourceLocation(_path, line, col))) ->
                            let _ = test.assertEqual(0)(temp)
                            in
                                let _ = test.assertEqual(1)(line)
                                in
                                    let _ = test.assertEqual(5)(col)
                                    in Unit
                        | _ -> test.fail("expected tagged return instruction")
                in
                    let dropInst = tagInstruction(RcDrop(0)("Int")(0)(false)(false)(None))(span)(Some(ctx))
                    in
                        let _ =
                            match dropInst with
                                | IrInstruction(RcDrop(temp, _tn, _sl, _res, _str, _dest), loc) ->
                                    let _ = test.assertEqual(0)(temp)
                                    in
                                        let _ = test.assertEqual(true)(loc == None)
                                        in Unit
                                | _ -> test.fail("expected unlocated drop instruction")
                        in Unit)

let testFunctionOriginsCreation unit =
    (let entry = createProgramEntryOrigin(Unit)
    in
        let _ =
            match entry with
                | IrFunctionOrigin(label, originKind, _src, _pLabel, Some(CompilerFunctionOwner(ownerKind, ownerName)), _disc, _loc) ->
                    let _ = test.assertEqual("_start_main")(label)
                    in
                        let _ = test.assertEqual(true)(originKind == ProgramEntryOrigin)
                        in
                            let _ = test.assertEqual(true)(ownerKind == ProgramFunctionOwner)
                            in
                                let _ = test.assertEqual("program entry")(ownerName)
                                in Unit
                | _ -> test.fail("unexpected program entry origin")
        in
            let srcOrigin =
                createSourceFunctionOrigin(
                    "myFunc",
                    Some("Module.myFunc"),
                    Some(
                        IrSourceLocation(
                            filePath = "Module.ash",
                            line = 10,
                            column = 5
                        )
                    ),
                    45
                )
            in
                let _ = test.assertEqual("myFunc")(srcOrigin.functionSourceName)
                in
                    let lambdaOrigin =
                        createLambdaOrigin(
                            "__closure_1",
                            "y",
                            TextSpan(50)(60),
                            None,
                            Some(entry),
                            Some(
                                IrSourceLocation(
                                    filePath = "Module.ash",
                                    line = 11,
                                    column = 1
                                )
                            )
                        )
                    in
                        let _ =
                            match lambdaOrigin with
                                | IrFunctionOrigin(label, originKind, _src, Some(pLabel), _owner, Some(disc), _loc) ->
                                    let _ = test.assertEqual("__closure_1")(label)
                                    in
                                        let _ = test.assertEqual(true)(originKind == ClosureHelperOrigin)
                                        in
                                            let _ = test.assertEqual("_start_main")(pLabel)
                                            in
                                                let _ = test.assertEqual("lambda:50:10:y")(disc)
                                                in Unit
                                | _ -> test.fail("unexpected lambda origin")
                        in Unit)

let testFunctionOriginsDiscovery unit =
    (let lambdaExpr = ExprLambda("x")(ExprVar("x"))(None)
    in
        let ast =
            ExprAt(
                TextSpan(0)(50),
                ExprLet(
                    "helper",
                    ExprAt(TextSpan(9)(25))(lambdaExpr),
                    ExprVar("helper"),
                    [],
                    None,
                    []
                )
            )
        in
            let ctx = createSourceContext("Main.ash")("let helper x = x in helper")
            in
                let origins = discoverSourceFunctionOrigins(ast)(None)(Some(ctx))
                in
                    let _ = test.assertEqual(1)(length(origins))
                    in
                        let _ =
                            match origins with
                                | (name, SourceFunctionOrigin(srcName, _q, Some(IrSourceLocation(_p, line, col)), offset)) :: _ ->
                                    let _ = test.assertEqual("helper")(name)
                                    in
                                        let _ = test.assertEqual("helper")(srcName)
                                        in
                                            let _ = test.assertEqual(1)(line)
                                            in
                                                let _ = test.assertEqual(10)(col)
                                                in
                                                    let _ = test.assertEqual(9)(offset)
                                                    in Unit
                                | _ -> test.fail("unexpected discovered origin structure")
                        in Unit)

let testHoverTypeInfo unit =
    (let semType =
        SemFunction(
            SemInt,
            SemInt,
            Some(
                SemRow(
                    [
                        SemCapability("Clock")([]),
                        SemCapability("FileSystem")([SemString])
                    ],
                    None
                )
            )
        )
    in
        let hover1 =
            HoverTypeInfo(
                span = TextSpan(0)(10),
                name = Some("getTime"),
                inferredType = semType,
                constraints = [],
                parameterNames = ["unit"],
                isParameter = false
            )
        in
            let hoverList = [hover1]
            in
                let found = findHoverAtOffset(5)(hoverList)
                in
                    let _ =
                        match found with
                            | Some(HoverTypeInfo(_s, Some(name), _t, _c, _p, _param)) -> test.assertEqual("getTime")(name)
                            | _ -> test.fail("expected to find hover info at offset 5")
                    in
                        let notFound = findHoverAtOffset(20)(hoverList)
                        in
                            let _ = test.assertEqual(true)(notFound == None)
                            in
                                let caps = collectTypeCapabilities(semType)
                                in
                                    let _ = test.assertEqual(2)(length(caps))
                                    in
                                        let authority = capturePublicAuthority(["getTime"])(hoverList)
                                        in
                                            let _ =
                                                match authority with
                                                    | PublicAuthorityRecord(name, authCaps) :: [] ->
                                                        let _ = test.assertEqual("getTime")(name)
                                                        in test.assertEqual(2)(length(authCaps))
                                                    | _ -> test.fail("unexpected public authority record")
                                            in Unit)

let testDecisionSnapshot unit =
    (let srcOrigin = createSourceFunctionOrigin("compute")(None)(None)(0)
    in
        let funcOrigin =
            IrFunctionOrigin(
                generatedLabel = "compute_0",
                originKind = SourceFunctionOriginKind,
                sourceOrigin = Some(srcOrigin),
                parentGeneratedLabel = None,
                compilerOwner = None,
                stableDiscriminator = None,
                generationLocation = None
            )
        in
            let ownershipRec =
                FunctionOwnershipRecord(
                    ordinal = 0,
                    origin = srcOrigin,
                    functionName = "compute",
                    parameters = ["x"],
                    borrowedParameters = ["x"],
                    consumedParameters = [],
                    uniqueParameters = [],
                    capturedValues = [],
                    resultProvenance = "Fresh",
                    resultAliases = [],
                    resultFresh = true,
                    resultPoisoned = false,
                    mayExecuteUnderLiveHandlerPost = false
                )
            in
                let placementRec =
                    ValuePlacementRecord(
                        ordinal = 0,
                        functionOrigin = Some(funcOrigin),
                        temp = 0,
                        placement = RuntimeRc,
                        ownership = TempOwned,
                        producer = ProducerCall,
                        dropKind = DropRc,
                        reason = ReasonInitialValue,
                        typeName = Some("List(Int)"),
                        location = None
                    )
                in
                    let snapshot =
                        CompilationDecisionSnapshot(
                            functionOwnership = [ownershipRec],
                            valuePlacements = [placementRec],
                            coroutineRepresentations = [],
                            patternBindings = [],
                            externalResources = [],
                            publicAuthority = [],
                            externalAuthority = []
                        )
                    in
                        let funcOwnerships = filterOwnershipFor(srcOrigin)(snapshot)
                        in
                            let _ = test.assertEqual(1)(length(funcOwnerships))
                            in
                                let placements = filterPlacementsIn("compute_0")(snapshot)
                                in
                                    let _ = test.assertEqual(1)(length(placements))
                                    in
                                        let cat = classifyValuePlacement(false)(false)(false)(true)(false)(false)
                                        in
                                            let _ = test.assertEqual(true)(cat == RuntimeRc)
                                            in Unit)

let runMetadataAndOriginsTests unit =
    (let _ = testSourceContextSingleFile(Unit)
    in
        let _ = testSourceContextMultiFile(Unit)
        in
            let _ = testRuntimeMachineryTagging(Unit)
            in
                let _ = testFunctionOriginsCreation(Unit)
                in
                    let _ = testFunctionOriginsDiscovery(Unit)
                    in
                        let _ = testHoverTypeInfo(Unit)
                        in
                            let _ = testDecisionSnapshot(Unit)
                            in
                                let _ = Ashes.IO.println("all self-hosted metadata and origins tests passed")
                                in Unit)
